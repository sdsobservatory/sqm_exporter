using Microsoft.Extensions.Options;
using Prometheus;
using System.Net.Sockets;
using System.Text;

Gauge SqmValues = Metrics.CreateGauge("sqm_reading", "SQM reading in mag/arcsec^2");
Gauge SqmPeriods = Metrics.CreateGauge("sqm_period", "Integration duration in seconds at time of most recent measurement");
Gauge SqmTemperature = Metrics.CreateGauge("sqm_temperature", "Temperature in C of the SQM device at time of most recent measurement");

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<SqmOptions>(builder.Configuration.GetSection("Sqm"));
builder.Services.AddMetricServer(options =>
{
    options.Port = 9100;
});
builder.Services.AddSingleton<ISqm, SqmLe>();

var app = builder.Build();

Metrics.SuppressDefaultMetrics();
Metrics.DefaultRegistry.AddBeforeCollectCallback(async (cancel) =>
{
    var sqm = app.Services.GetRequiredService<ISqm>();
    var reading = await sqm.GetReading(cancel);
    SqmValues.Set(reading.Value);
    SqmPeriods.Set(reading.Period.TotalSeconds);
    SqmTemperature.Set(reading.Temperature);
});

app.Run();

internal record SqmOptions
{
    public required string Hostname { get; init; }
    public required int Port { get; init; }
}

internal record SqmReading
{
    public required double Value { get; init; }
    public required int Frequency { get; init; }
    public required int PeriodCounts { get; init; }
    public required TimeSpan Period { get; init; }
    public required double Temperature { get; init; }
    public required DateTime Timestamp { get; init; }
}

internal interface ISqm
{
    Task<SqmReading> GetReading(CancellationToken token);
}

internal class SqmLe : ISqm
{
    private readonly ILogger<SqmLe> _logger;
    private readonly IOptions<SqmOptions> _options;
    private const int ReadingLength = 57;

    public SqmLe(
        ILogger<SqmLe> logger,
        IOptions<SqmOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task<SqmReading> GetReading(CancellationToken token)
    {
        using var client = new TcpClient(_options.Value.Hostname, _options.Value.Port);
        await using var stream = client.GetStream();

        ReadOnlyMemory<byte> outBuffer = Encoding.ASCII.GetBytes("ux");
        Memory<byte> inBuffer = new byte[128];
        
        await stream.WriteAsync(outBuffer, token);
        await stream.ReadAtLeastAsync(inBuffer, ReadingLength, cancellationToken: token);
        string response = Encoding.ASCII.GetString(inBuffer.Span)[..^3];

        var reading = new SqmReading
        {
            Value = double.Parse(response[2..8]),
            Frequency = int.Parse(response[10..20]),
            PeriodCounts = int.Parse(response[23..31]),
            Period = TimeSpan.FromSeconds(double.Parse(response[35..46])),
            Temperature = double.Parse(response[48..54]),
            Timestamp = DateTime.UtcNow,
        };

        _logger.LogTrace(reading.ToString());
        return reading;
    }
}