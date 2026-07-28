namespace Sample.Web
{
    public interface IServiceCollection { }

    public sealed class Startup
    {
        public void Configure(IServiceCollection services)
        {
            services.AddSingleton<ICache, MemoryCache>();
            services.AddScoped<IOrderStore, Sample.Infrastructure.SqlOrderStore>();
        }
    }

    public interface ICache { }
    public interface IOrderStore { }

    public sealed class MemoryCache : ICache
    {
        public MemoryCache(IOrderStore store) { }
    }
}
