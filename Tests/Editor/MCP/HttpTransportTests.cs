using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UniClaude.Editor.MCP;

namespace UniClaude.Editor.Tests.MCP
{
    public class HttpTransportTests
    {
        const string TestToken = "0123456789abcdef0123456789abcdef";

        HttpTransport _transport;

        [SetUp]
        public void SetUp()
        {
            _transport = new HttpTransport();
            _transport.SetAuthToken(TestToken);
        }

        [TearDown]
        public void TearDown()
        {
            _transport?.Dispose();
        }

        static StringContent JsonBody(string s) =>
            new StringContent(s, Encoding.UTF8, "application/json");

        static HttpRequestMessage AuthedRequest(HttpMethod method, string url)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
            return req;
        }

        [Test]
        public void Start_SetsIsRunningTrue()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);
            Assert.IsTrue(_transport.IsRunning);
        }

        [Test]
        public void Start_AssignsEndpoint()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);
            Assert.IsNotNull(_transport.Endpoint);
            Assert.That(_transport.Endpoint, Does.StartWith("http://127.0.0.1:"));
        }

        [Test]
        public void Stop_SetsIsRunningFalse()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);
            _transport.Stop();
            Assert.IsFalse(_transport.IsRunning);
        }

        [Test]
        public void Start_WithoutHandler_ThrowsInvalidOperation()
        {
            Assert.Throws<InvalidOperationException>(() => _transport.Start(0));
        }

        [Test]
        public async Task RPC_RoundTrip_ReturnsHandlerResponse()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"echo\":true}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var req = AuthedRequest(HttpMethod.Post, _transport.Endpoint + "rpc");
            req.Content = JsonBody("{\"test\":1}");
            using var response = await client.SendAsync(req);
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual("{\"echo\":true}", body);
        }

        [Test]
        public async Task RPC_Returns404_ForUnknownPath()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var req = AuthedRequest(HttpMethod.Get, _transport.Endpoint + "unknown");
            using var response = await client.SendAsync(req);

            Assert.AreEqual(404, (int)response.StatusCode);
        }

        [Test]
        public async Task RPC_Returns401_WhenAuthHeaderMissing()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var response = await client.PostAsync(
                _transport.Endpoint + "rpc",
                JsonBody("{\"x\":1}"));
            Assert.AreEqual(401, (int)response.StatusCode);
        }

        [Test]
        public async Task RPC_Returns401_WhenAuthHeaderWrong()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, _transport.Endpoint + "rpc");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
            req.Content = JsonBody("{\"x\":1}");
            using var response = await client.SendAsync(req);
            Assert.AreEqual(401, (int)response.StatusCode);
        }

        [Test]
        public async Task RPC_AcceptsTokenViaQueryString()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{\"ok\":true}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var response = await client.PostAsync(
                $"{_transport.Endpoint}rpc?token={TestToken}",
                JsonBody("{}"));
            Assert.AreEqual(200, (int)response.StatusCode);
        }

        [Test]
        public async Task RPC_Returns403_WhenHostHeaderIsNonLoopback()
        {
            _transport.SetRequestHandler(json => Task.FromResult("{}"));
            _transport.Start(0);

            using var client = new HttpClient();
            using var req = AuthedRequest(HttpMethod.Post, _transport.Endpoint + "rpc");
            req.Headers.Host = "evil.example.com";
            req.Content = JsonBody("{}");
            using var response = await client.SendAsync(req);
            Assert.AreEqual(403, (int)response.StatusCode);
        }

        [Test]
        public async Task RPC_Returns503_WhenServerHasNoAuthToken()
        {
            using var transport = new HttpTransport();
            transport.SetRequestHandler(json => Task.FromResult("{}"));
            // Intentionally do NOT call SetAuthToken — server should refuse all requests.
            transport.Start(0);

            using var client = new HttpClient();
            using var response = await client.PostAsync(transport.Endpoint + "rpc", JsonBody("{}"));
            Assert.AreEqual(503, (int)response.StatusCode);
        }
    }
}
