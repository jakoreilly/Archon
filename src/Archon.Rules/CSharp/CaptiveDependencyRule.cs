using Archon.Core.Findings;
using Archon.Core.Rules;
using Archon.Core.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Archon.Rules.CSharp;

/// <summary>
/// Flags a service that outlives a dependency it holds, which keeps the shorter-lived object
/// alive for the longer lifetime.
///
/// Registrations are recognised by method name and types are matched on the identifier text as
/// written, so two distinct types sharing a simple name across namespaces could be conflated and
/// open generics are skipped. A finding is only produced when both lifetimes are known from a
/// visible literal registration, which keeps the rule silent rather than speculative when the
/// registration is built dynamically.
/// </summary>
public sealed class CaptiveDependencyRule : IRule
{
    /// <summary>A singleton holding a scoped service, which scope validation rejects at runtime.</summary>
    public const string SingletonCapturesScoped = "AR0002";

    /// <summary>A singleton holding a transient service, making that service effectively singleton.</summary>
    public const string SingletonCapturesTransient = "AR0003";

    /// <summary>A scoped service holding a transient service for the whole scope.</summary>
    public const string ScopedCapturesTransient = "AR0004";

    /// <summary>A constructor dependency with no statically visible registration.</summary>
    public const string UnregisteredDependency = "AR0005";

    private const string Lifetimes = "lifetime";

    public IReadOnlyList<RuleDescriptor> Descriptors { get; } = new[]
    {
        new RuleDescriptor(
            SingletonCapturesScoped,
            "Singleton captures scoped service",
            Lifetimes,
            Severity.Error,
            "A singleton holds a scoped service, which scope validation rejects at runtime."),
        new RuleDescriptor(
            SingletonCapturesTransient,
            "Singleton captures transient service",
            Lifetimes,
            Severity.Warning,
            "A singleton holds a transient service, so that service never leaves memory."),
        new RuleDescriptor(
            ScopedCapturesTransient,
            "Scoped service captures transient service",
            Lifetimes,
            Severity.Information,
            "A scoped service holds a transient service for the lifetime of the scope."),
        new RuleDescriptor(
            UnregisteredDependency,
            "Dependency has no visible registration",
            Lifetimes,
            Severity.Off,
            "A constructor parameter matches no registration found in the workspace.")
    };

    public RuleScope Scope => RuleScope.Workspace;

    public string Language => RuleLanguages.CSharp;

    private enum Lifetime
    {
        Transient = 0,
        Scoped = 1,
        Singleton = 2
    }

    private static readonly Dictionary<string, Lifetime> MethodLifetimes = new(StringComparer.Ordinal)
    {
        ["AddSingleton"] = Lifetime.Singleton,
        ["AddScoped"] = Lifetime.Scoped,
        ["AddTransient"] = Lifetime.Transient
    };

    private static readonly string[] FrameworkTypeAllowlist =
    {
        "ILogger", "IOptions", "IOptionsSnapshot", "IOptionsMonitor",
        "IConfiguration", "IServiceProvider", "IHttpContextAccessor"
    };

    private sealed record Registration(
        string ServiceType,
        string? ImplementationType,
        Lifetime Lifetime,
        string FilePath,
        LinePositionSpan Span);

    public IEnumerable<Finding> Analyze(RuleContext context)
    {
        bool checkUnregistered = context.IsEnabled(UnregisteredDependency);

        var typesByName = new Dictionary<string, TypeDeclarationSyntax>(StringComparer.Ordinal);
        var registrations = new List<Registration>();

        foreach (SourceFile file in context.Workspace.FilesOfLanguage(RuleLanguages.CSharp))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            ParsedCSharp? parsed = context.Sources.GetCSharp(file.Path);
            if (parsed is null)
            {
                continue;
            }

            foreach (TypeDeclarationSyntax type in parsed.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                typesByName.TryAdd(type.Identifier.Text, type);
            }
            registrations.AddRange(Harvest(file.Path, parsed));
        }

        var lifetimeByService = new Dictionary<string, Lifetime>(StringComparer.Ordinal);
        foreach (Registration registration in registrations)
        {
            lifetimeByService[SimpleName(registration.ServiceType)] = registration.Lifetime;
        }

        var findings = new List<Finding>();
        foreach (Registration registration in registrations)
        {
            if (registration.ImplementationType is null)
            {
                continue;
            }
            if (!typesByName.TryGetValue(SimpleName(registration.ImplementationType), out TypeDeclarationSyntax? implementation))
            {
                continue;
            }
            ConstructorDeclarationSyntax? constructor = SelectConstructor(implementation);
            if (constructor is null)
            {
                continue;
            }

            foreach (ParameterSyntax parameter in constructor.ParameterList.Parameters)
            {
                if (parameter.Type is null)
                {
                    continue;
                }
                string parameterType = SimpleName(parameter.Type.ToString());

                if (lifetimeByService.TryGetValue(parameterType, out Lifetime dependencyLifetime))
                {
                    if (registration.Lifetime > dependencyLifetime)
                    {
                        findings.Add(CreateCaptive(registration, parameterType, dependencyLifetime));
                    }
                    continue;
                }

                if (checkUnregistered && LooksLikeInterface(parameterType) && !FrameworkTypeAllowlist.Contains(parameterType))
                {
                    findings.Add(new Finding
                    {
                        RuleId = UnregisteredDependency,
                        FilePath = registration.FilePath,
                        Kind = "UnregisteredDependency",
                        Span = ToSpan(registration.Span),
                        Message = $"'{parameterType}' was not found among statically visible registrations; it may be registered by a library extension method."
                    });
                }
            }
        }
        return findings;
    }

    private static IEnumerable<Registration> Harvest(string filePath, ParsedCSharp parsed)
    {
        foreach (InvocationExpressionSyntax invocation in parsed.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
            {
                continue;
            }
            string methodName = member.Name is GenericNameSyntax generic
                ? generic.Identifier.Text
                : member.Name.Identifier.Text;
            if (!MethodLifetimes.TryGetValue(methodName, out Lifetime lifetime))
            {
                continue;
            }

            (string? service, string? implementation) = ExtractTypes(invocation, member);
            if (service is null)
            {
                continue;
            }

            yield return new Registration(
                Normalize(service),
                implementation is null ? null : Normalize(implementation),
                lifetime,
                filePath,
                parsed.Tree.GetLineSpan(invocation.Span).Span);
        }
    }

    private static (string? Service, string? Implementation) ExtractTypes(
        InvocationExpressionSyntax invocation,
        MemberAccessExpressionSyntax member)
    {
        if (member.Name is GenericNameSyntax generic)
        {
            SeparatedSyntaxList<TypeSyntax> arguments = generic.TypeArgumentList.Arguments;
            if (arguments.Count == 2)
            {
                return (arguments[0].ToString(), arguments[1].ToString());
            }
            if (arguments.Count == 1)
            {
                bool hasFactoryArgument = invocation.ArgumentList.Arguments
                    .Any(a => a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);
                string service = arguments[0].ToString();
                return hasFactoryArgument ? (service, null) : (service, service);
            }
            return (null, null);
        }

        List<string> typeofArguments = invocation.ArgumentList.Arguments
            .Select(a => a.Expression as TypeOfExpressionSyntax)
            .Where(t => t is not null)
            .Select(t => t!.Type.ToString())
            .ToList();

        return typeofArguments.Count switch
        {
            2 => (typeofArguments[0], typeofArguments[1]),
            1 => (typeofArguments[0], typeofArguments[0]),
            _ => (null, null)
        };
    }

    private static ConstructorDeclarationSyntax? SelectConstructor(TypeDeclarationSyntax type) =>
        type.Members.OfType<ConstructorDeclarationSyntax>()
            .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .OrderByDescending(c => c.ParameterList.Parameters.Count)
            .FirstOrDefault();

    private static bool LooksLikeInterface(string typeName) =>
        typeName.Length > 1 && typeName[0] == 'I' && char.IsUpper(typeName[1]);

    /// <summary>Strips generic arguments and any qualification, leaving the bare matching key.</summary>
    private static string SimpleName(string typeText)
    {
        string text = typeText.Trim().TrimEnd('?');
        int genericStart = text.IndexOf('<');
        if (genericStart >= 0)
        {
            text = text[..genericStart];
        }
        int lastDot = text.LastIndexOf('.');
        return lastDot >= 0 ? text[(lastDot + 1)..] : text;
    }

    private static string Normalize(string typeText)
    {
        string trimmed = typeText.Trim();
        const string globalPrefix = "global::";
        return trimmed.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? trimmed[globalPrefix.Length..]
            : trimmed;
    }

    private static Finding CreateCaptive(Registration registration, string dependencyType, Lifetime dependencyLifetime)
    {
        string ruleId = (registration.Lifetime, dependencyLifetime) switch
        {
            (Lifetime.Singleton, Lifetime.Scoped) => SingletonCapturesScoped,
            (Lifetime.Singleton, Lifetime.Transient) => SingletonCapturesTransient,
            _ => ScopedCapturesTransient
        };

        string message = (registration.Lifetime, dependencyLifetime) switch
        {
            (Lifetime.Singleton, Lifetime.Scoped) =>
                $"'{registration.ImplementationType}' (Singleton) captures scoped service '{dependencyType}' for the process lifetime; scope validation will throw.",
            (Lifetime.Singleton, Lifetime.Transient) =>
                $"'{registration.ImplementationType}' (Singleton) captures transient service '{dependencyType}', which becomes effectively singleton.",
            _ =>
                $"'{registration.ImplementationType}' ({registration.Lifetime}) captures transient service '{dependencyType}' for the scope's lifetime."
        };

        return new Finding
        {
            RuleId = ruleId,
            FilePath = registration.FilePath,
            Kind = "CaptiveDependency",
            Span = ToSpan(registration.Span),
            Message = message
        };
    }

    private static SourceSpan ToSpan(LinePositionSpan span) =>
        new(span.Start.Line, span.Start.Character, span.End.Line, span.End.Character);
}
