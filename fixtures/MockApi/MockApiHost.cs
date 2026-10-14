using System.Collections.Immutable;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace MockApi;

/// <summary>
/// The mock API: one process, one listener per API. Two listeners stand for two APIs (task 5.3), and
/// each validates the bearer token itself.
///
/// It references no <c>Platform.*</c> project, which check A-02 enforces. That is not tidiness: the
/// mock API's job is to show that an upstream validates the token on its own (assumption A5), and an
/// upstream sharing the gateway's code would prove nothing.
///
/// One listener per API rather than one listener and a route prefix, because the two APIs differ in
/// how they validate: <c>orders</c> binds the audience and <c>reports</c> does not, and that is a
/// property of the listener's token validation rather than of a route.
/// </summary>
public sealed class MockApiHost : IAsyncDisposable
{
    private readonly ImmutableArray<RunningListener> listeners;

    private MockApiHost(ImmutableArray<RunningListener> listeners) => this.listeners = listeners;

    /// <summary>Starts every listener the options name.</summary>
    public static async Task<MockApiHost> StartAsync(MockApiOptions options, CancellationToken ct)
    {
        List<RunningListener> started = [];

        foreach (ApiListenerOptions listener in options.Listeners)
        {
            started.Add(await RunningListener.StartAsync(options, listener, ct));
        }

        return new MockApiHost([.. started]);
    }

    /// <summary>Where one listener is listening, for a test that has let the system pick the port.</summary>
    public Uri BaseAddressOf(string name) => Listener(name).BaseAddress;

    /// <summary>One listener's admitted clients, to admit or withdraw one while it runs (F-07).</summary>
    public ClientAdmission AdmissionOf(string name) => Listener(name).Admission;

    public async Task WaitForShutdownAsync() =>
        await Task.WhenAll(listeners.Select(listener => listener.WaitForShutdownAsync()));

    public async ValueTask DisposeAsync()
    {
        foreach (RunningListener listener in listeners)
        {
            await listener.DisposeAsync();
        }
    }

    private RunningListener Listener(string name) =>
        listeners.FirstOrDefault(listener => listener.Name == name)
        ?? throw new KeyNotFoundException($"no listener is called {name}.");

    private sealed class RunningListener : IAsyncDisposable
    {
        private readonly WebApplication app;

        private RunningListener(string name, WebApplication app, Uri baseAddress, ClientAdmission admission)
        {
            Name = name;
            this.app = app;
            BaseAddress = baseAddress;
            Admission = admission;
        }

        internal string Name { get; }

        internal Uri BaseAddress { get; }

        internal ClientAdmission Admission { get; }

        internal static async Task<RunningListener> StartAsync(
            MockApiOptions options, ApiListenerOptions listener, CancellationToken ct)
        {
            ClientAdmission admission = new(listener.AdmittedClients);

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{listener.Port}");
            builder.Services.AddSingleton(listener);
            builder.Services.AddSingleton(admission);

            // Signature, issuer, expiry, and the audience when this API binds one. Everything here is
            // the library's; the mock API hand-writes no token validation.
            builder.Services.AddAuthentication().AddJwtBearer(bearer =>
            {
                bearer.MetadataAddress = options.MetadataAddress;
                bearer.RequireHttpsMetadata = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,

                    // No skew. A test that shortens a token's lifetime to prove expiry is refused
                    // would otherwise wait out the library's five-minute default.
                    ClockSkew = TimeSpan.Zero,

                    // Null means this API checks no audience, because its app passes no resource and
                    // its tokens carry no aud at all.
                    ValidateAudience = listener.Audience is not null,
                    ValidAudience = listener.Audience,
                };

                bearer.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // Cognito marks what a token is for. An ID token presented as a bearer token
                        // carries the same signature and issuer, so this is the only check that
                        // separates them.
                        string? use = context.Principal?.FindFirst("token_use")?.Value;
                        if (use != "access")
                        {
                            context.Fail($"token_use is '{use}', not 'access'.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

            builder.Services.AddAuthorization();

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            Routes.Map(app);

            await app.StartAsync(ct);

            string address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException($"the {listener.Name} listener started without an address.");

            return new RunningListener(listener.Name, app, new Uri(address), admission);
        }

        internal Task WaitForShutdownAsync() => app.WaitForShutdownAsync();

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
