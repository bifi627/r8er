var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

// ponytail: marker so integration tests can use WebApplicationFactory<Program>
public partial class Program;
