// See https://aka.ms/new-console-template for more information

using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddSingleton(sp => new SqlMethods("guid_keys", CreateConnString(1), sp.GetRequiredService<ILogger<SqlMethods>>()))
    .AddSingleton(sp => new SqlMethods("seq_keys", CreateConnString(2), sp.GetRequiredService<ILogger<SqlMethods>>()))
    .AddSingleton(sp => new SqlMethods("broken_keys", CreateConnString(3), sp.GetRequiredService<ILogger<SqlMethods>>()))
    .AddHostedService<TestSetupService>();

var app = builder.Build();

app.UseMetricServer();
app.MapGet("/", () => "Hello World!");
app.Run();

static string CreateConnString(int n)
{
    return
        $"Data Source=localhost; Port={n}3306; Character Set=utf8mb4; User Id=root; Password=root; convertzerodatetime=true; Allow User Variables=True; Pooling=true; Max Pool Size=400;SSL Mode=Preferred; ConnectionIdleTimeout=120; CancellationTimeout=-1; ConnectionTimeout=10; DefaultCommandTimeout=20; Keepalive=10; ServerRedirectionMode=Preferred;";
}