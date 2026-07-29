// Deliberately trips both sample rules. There is no project file here: analysis is syntax-only,
// so this file does not need to compile or belong to anything to be analysed.

using System;
using System.Data.SqlClient;
using System.Net.Http;

namespace Example;

public sealed class Orders
{
    public void Fetch()
    {
        // ACME0001: a client per call, rather than one asked for from IHttpClientFactory.
        var client = new HttpClient();
        Console.WriteLine(client);
    }

    public void Load()
    {
        // The using directive above is what ACME0002 reports; this is here to show why it matters.
        var connection = new SqlConnection("Server=.;Database=Orders;Integrated Security=true");
        Console.WriteLine(connection);
    }
}
