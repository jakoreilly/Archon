using System;
using System.Globalization;
using System.IO;

namespace ConventionsExample;

public sealed class AmbientUsage
{
    private readonly Clock clock = new();

    public void ReadEnvironmentAmbiently()
    {
        var localTime = DateTime.Now;
        var offsetTime = DateTimeOffset.Now;
        var today = DateTime.Today;
        var qualifiedTime = System.DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        var workingDirectory = Directory.GetCurrentDirectory();
    }

    public void ReadEnvironmentProperly()
    {
        // Controls: every line below must stay silent.
        var utcTime = DateTime.UtcNow;
        var utcOffsetTime = DateTimeOffset.UtcNow;
        var invariant = CultureInfo.InvariantCulture;
        DateTime now = DateTime.UtcNow;
        var instanceTime = clock.Now;
    }
}

public sealed class Clock
{
    public DateTime Now => DateTime.UtcNow;
}
