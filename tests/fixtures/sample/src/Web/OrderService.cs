using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sample.Web
{
    public sealed class OrderService
    {
        private readonly string _query = "SELECT * FROM dbo.Orders";

        public void Sync()
        {
            var value = LoadAsync().Result;
            SaveAsync();
        }

        public async void FireAndForget()
        {
            await Task.Delay(1);
        }

        public bool HasAny(IEnumerable<int> items) => items.Count() > 0;

        public string Join(IEnumerable<string> parts)
        {
            string result = "";
            foreach (var part in parts)
            {
                result += part;
            }
            return result;
        }

        public void Swallow()
        {
            try { Sync(); } catch { }
        }

        private Task<int> LoadAsync() => Task.FromResult(1);
        private Task SaveAsync() => Task.CompletedTask;
    }
}
