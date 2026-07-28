using System;
using Sample.Infrastructure;

namespace Sample.Web
{
    public sealed class HomeController
    {
        private readonly SqlOrderStore _store = new Sample.Infrastructure.SqlOrderStore();

        public void Index() => Console.WriteLine(_store);
    }
}
