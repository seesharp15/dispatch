using Dispatch.Web.Data;
using FeedDiscovery;
using Dispatch.Web.Models;
using Dispatch.Web.Options;
using Dispatch.Web.Services;
using Dispatch.Web.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Json;
using System.Text.Json.Serialization;
using System.Data;

string ResolveRecordingPath(string path, IHostEnvironment env)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
}

double ComputeTranscriptProgress(Recording recording, TranscriptionOptions options, long? fileSizeBytes)
{
    return recording.TranscriptStatus switch
    {
        TranscriptStatus.Complete => 100,
        TranscriptStatus.Skipped => 100,
        TranscriptStatus.Failed => 0,
        TranscriptStatus.Processing => EstimateProcessingProgress(recording, options, fileSizeBytes),
        _ => 0
    };
}

double EstimateProcessingProgress(Recording recording, TranscriptionOptions options, long? fileSizeBytes)
{
    if (recording.TranscriptStartedUtc == null || recording.DurationSeconds <= 0)
    {
        if (recording.TranscriptStartedUtc == null)
        {
            return 5;
        }
    }

    var rate = Math.Max(options.ExpectedRealtimeFactor, 0.1);
    var expectedAudioSeconds = EstimateDurationFromFileSize(fileSizeBytes, options);
    if (expectedAudioSeconds <= 0 && recording.DurationSeconds > 0)
    {
        expectedAudioSeconds = recording.DurationSeconds;
    }

    if (expectedAudioSeconds <= 0)
    {
        expectedAudioSeconds = 1;
    }

    var expectedSeconds = Math.Max(expectedAudioSeconds / rate, 1);
    var elapsed = (DateTime.UtcNow - recording.TranscriptStartedUtc.Value).TotalSeconds;
    var percent = Math.Min(99, (elapsed / expectedSeconds) * 100);
    return Math.Max(5, percent);
}

double EstimateDurationFromFileSize(long? fileSizeBytes, TranscriptionOptions options)
{
    if (fileSizeBytes == null || fileSizeBytes <= 0)
    {
        return 0;
    }

    // WAV header is ~44 bytes for PCM.
    var audioBytes = Math.Max(0, fileSizeBytes.Value - 44);
    var bytesPerSecond = Math.Max(options.EstimatedBytesPerSecond, 1000);
    return audioBytes / (double)bytesPerSecond;
}

long? GetRecordingFileSize(Recording recording, IWebHostEnvironment env)
{
    if (string.IsNullOrWhiteSpace(recording.FilePath))
    {
        return null;
    }

    var path = ResolveRecordingPath(recording.FilePath, env);
    try
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        return info.Length;
    }
    catch
    {
        return null;
    }
}

async Task<Dictionary<Guid, int>> GetPendingQueuePositionsAsync(DispatchDbContext db, CancellationToken cancellationToken)
{
    var pendingIds = await db.Recordings.AsNoTracking()
        .Where(r => r.TranscriptStatus == TranscriptStatus.Pending)
        .OrderBy(r => r.CreatedUtc)
        .Select(r => r.Id)
        .ToListAsync(cancellationToken);

    var map = new Dictionary<Guid, int>(pendingIds.Count);
    for (var i = 0; i < pendingIds.Count; i++)
    {
        map[pendingIds[i]] = i + 1;
    }

    return map;
}

var builder = WebApplication.CreateBuilder(args);

string ResolvePath(string basePath, string path)
{
    if (string.IsNullOrWhiteSpace(path))
    {
        return path;
    }

    return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path));
}

builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.PostConfigure<StorageOptions>(options =>
{
    var root = builder.Environment.ContentRootPath;
    options.RootPath = ResolvePath(root, options.RootPath);
    options.RecordingsPath = ResolvePath(root, options.RecordingsPath);
    options.DatabasePath = ResolvePath(root, options.DatabasePath);
});
builder.Services.Configure<SegmentationOptions>(builder.Configuration.GetSection("Segmentation"));
builder.Services.Configure<StreamOptions>(builder.Configuration.GetSection("Stream"));
builder.Services.Configure<BroadcastifyOptions>(builder.Configuration.GetSection("Broadcastify"));
builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection("Transcription"));
builder.Services.Configure<DecoderOptions>(builder.Configuration.GetSection("Decoder"));
builder.Services.AddBroadcastifyFeedDiscovery(builder.Configuration);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var storageOptions = builder.Configuration.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
storageOptions.RootPath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.RootPath);
storageOptions.RecordingsPath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.RecordingsPath);
storageOptions.DatabasePath = ResolvePath(builder.Environment.ContentRootPath, storageOptions.DatabasePath);

Directory.CreateDirectory(storageOptions.RootPath);
Directory.CreateDirectory(storageOptions.RecordingsPath);
var dbDirectory = Path.GetDirectoryName(storageOptions.DatabasePath);
if (!string.IsNullOrWhiteSpace(dbDirectory))
{
    Directory.CreateDirectory(dbDirectory);
}

builder.Services.AddDbContext<DispatchDbContext>(options =>
    options.UseSqlite($"Data Source={storageOptions.DatabasePath}"));

builder.Services.AddHttpClient("stream")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DispatchFeedMonitor/1.0");
    });

builder.Services.AddSingleton<BroadcastifyResolver>();
builder.Services.AddSingleton<FeedRecorder>();
builder.Services.AddSingleton<FeedCoordinator>();

builder.Services.AddSingleton<ITranscriber>(sp =>
{
    var options = sp.GetRequiredService<IOptions<TranscriptionOptions>>().Value;
    if (!options.Enabled)
    {
        return new NoopTranscriber();
    }

    return options.Provider.Equals("whisper-cli", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<WhisperCliTranscriber>()
        : new NoopTranscriber();
});

builder.Services.AddSingleton<WhisperCliTranscriber>();
builder.Services.AddHostedService<TranscriptionWorker>();
builder.Services.AddHostedService<FeedStartupWorker>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DispatchDbContext>();
    db.Database.EnsureCreated();

    var connection = db.Database.GetDbConnection();
    connection.Open();
    var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Recordings');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            existingColumns.Add(reader.GetString(1));
        }
    }

    if (!existingColumns.Contains("TranscriptStartedUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN TranscriptStartedUtc TEXT NULL;");
    }
}

app.MapGet("/api/feeds", async (DispatchDbContext db, FeedCoordinator coordinator) =>
{
    var feeds = await db.Feeds.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
    var response = feeds.Select(feed => new FeedDto(
        feed.Id,
        feed.Name,
        feed.BroadcastifyUrl,
        feed.StreamUrl,
        feed.FeedIdentifier,
        feed.IsActive,
        coordinator.IsRunning(feed.Id),
        feed.CreatedUtc,
        feed.LastStartedUtc,
        feed.LastStoppedUtc));

    return Results.Ok(response);
});

app.MapPost("/api/feeds", async (AddFeedRequest request, DispatchDbContext db, BroadcastifyResolver resolver) =>
{
    if (!resolver.TryResolve(request.BroadcastifyUrl, out var feedId, out var streamUrl))
    {
        return Results.BadRequest(new { message = "Unable to parse Broadcastify feed URL or ID." });
    }

    var name = string.IsNullOrWhiteSpace(request.Name) ? $"Feed {feedId}" : request.Name.Trim();
    var feed = new Feed
    {
        Id = Guid.NewGuid(),
        Name = name,
        BroadcastifyUrl = request.BroadcastifyUrl.Trim(),
        StreamUrl = streamUrl,
        FeedIdentifier = feedId,
        IsActive = false,
        CreatedUtc = DateTime.UtcNow
    };

    db.Feeds.Add(feed);
    await db.SaveChangesAsync();

    return Results.Ok(new FeedDto(
        feed.Id,
        feed.Name,
        feed.BroadcastifyUrl,
        feed.StreamUrl,
        feed.FeedIdentifier,
        feed.IsActive,
        false,
        feed.CreatedUtc,
        feed.LastStartedUtc,
        feed.LastStoppedUtc));
});

app.MapPost("/api/feeds/{id:guid}/start", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = true;
    feed.LastStartedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StartAsync(feed, ct);

    return Results.Ok();
});

app.MapPost("/api/feeds/{id:guid}/stop", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = false;
    feed.LastStoppedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StopAsync(feed.Id);

    return Results.Ok();
});

app.MapGet("/api/feeds/{id:guid}/recordings", async (Guid id, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    var recordings = await db.Recordings.AsNoTracking()
        .Where(r => r.FeedId == id)
        .OrderByDescending(r => r.StartUtc)
        .Take(250)
        .ToListAsync(ct);

    var queuePositions = await GetPendingQueuePositionsAsync(db, ct);

    var response = recordings.Select(r => new RecordingDto(
        r.Id,
        r.FeedId,
        r.FilePath,
        r.StartUtc,
        r.EndUtc,
        r.DurationSeconds,
        r.TranscriptStatus,
        ComputeTranscriptProgress(r, options.Value, GetRecordingFileSize(r, env)),
        queuePositions.TryGetValue(r.Id, out var position) ? position : null,
        r.TranscriptText,
        r.TranscriptPath,
        r.TranscriptProvider));

    return Results.Ok(response);
});

app.MapGet("/api/recordings/{id:guid}/audio", async (Guid id, DispatchDbContext db, IWebHostEnvironment env) =>
{
    var recording = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
    if (recording == null)
    {
        return Results.NotFound();
    }

    var filePath = ResolveRecordingPath(recording.FilePath, env);

    if (!File.Exists(filePath))
    {
        return Results.NotFound();
    }

    return Results.File(filePath, "audio/wav", enableRangeProcessing: true);
});

app.MapGet("/api/recordings/{id:guid}", async (Guid id, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    var recording = await db.Recordings.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    var queuePositions = await GetPendingQueuePositionsAsync(db, ct);

    return Results.Ok(new RecordingDto(
        recording.Id,
        recording.FeedId,
        recording.FilePath,
        recording.StartUtc,
        recording.EndUtc,
        recording.DurationSeconds,
        recording.TranscriptStatus,
        ComputeTranscriptProgress(recording, options.Value, GetRecordingFileSize(recording, env)),
        queuePositions.TryGetValue(recording.Id, out var position) ? position : null,
        recording.TranscriptText,
        recording.TranscriptPath,
        recording.TranscriptProvider));
});

app.MapGet("/api/stream", async (string url, BroadcastifyResolver resolver, IHttpClientFactory httpClientFactory, HttpContext context, CancellationToken ct) =>
{
    if (!resolver.TryResolve(url, out _, out var streamUrl))
    {
        return Results.BadRequest(new { message = "Invalid Broadcastify feed URL or ID." });
    }

    var client = httpClientFactory.CreateClient("stream");
    using var response = await client.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
    response.EnsureSuccessStatusCode();

    context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
    await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
    await responseStream.CopyToAsync(context.Response.Body, ct);
    return Results.Empty;
});

app.MapFallbackToFile("index.html");

app.Run();
