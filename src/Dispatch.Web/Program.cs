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
using System.Globalization;
using System.Text.Json;
using System.Diagnostics;
using DiscoveryBroadcastifyOptions = FeedDiscovery.Broadcastify.BroadcastifyOptions;

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

DateTime AsUtc(DateTime value)
{
    return value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);
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

async Task<(Dictionary<Guid, int> Map, int Total)> GetPendingQueueAsync(DispatchDbContext db, CancellationToken cancellationToken)
{
    var pendingIds = await db.Recordings.AsNoTracking()
        .Where(r => r.TranscriptStatus == TranscriptStatus.Pending && !r.IsArchived)
        .OrderBy(r => r.CreatedUtc)
        .Select(r => r.Id)
        .ToListAsync(cancellationToken);

    var map = new Dictionary<Guid, int>(pendingIds.Count);
    for (var i = 0; i < pendingIds.Count; i++)
    {
        map[pendingIds[i]] = i + 1;
    }

    return (map, pendingIds.Count);
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
builder.Services.AddSingleton<ILocalAudioFeedProvider, LocalAudioFeedProvider>();
builder.Services.AddSingleton<IRecordingEventHub, RecordingEventHub>();
builder.Services.AddSingleton<IFeedEventHub, FeedEventHub>();
builder.Services.AddSingleton<IDailyTranscriptSynthesizer, ExtractiveDailyTranscriptSynthesizer>();
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
    var recordingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Recordings');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            recordingColumns.Add(reader.GetString(1));
        }
    }

    if (!recordingColumns.Contains("TranscriptStartedUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN TranscriptStartedUtc TEXT NULL;");
    }

    if (!recordingColumns.Contains("IsArchived"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN IsArchived INTEGER NOT NULL DEFAULT 0;");
    }

    if (!recordingColumns.Contains("ArchivedUtc"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Recordings ADD COLUMN ArchivedUtc TEXT NULL;");
    }

    var feedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "PRAGMA table_info('Feeds');";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            feedColumns.Add(reader.GetString(1));
        }
    }

    if (!feedColumns.Contains("IsVisible"))
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE Feeds ADD COLUMN IsVisible INTEGER NOT NULL DEFAULT 1;");
    }
}

app.MapGet("/api/discovery/states", (IOptions<DiscoveryBroadcastifyOptions> options) =>
{
    var states = options.Value.StateIdMap.Keys
        .OrderBy(s => s)
        .ToList();

    return Results.Ok(states);
});

app.MapGet("/api/discovery/feeds", async (string state, string? county, IFeedDiscoveryService service, CancellationToken ct) =>
{
    var feeds = string.IsNullOrWhiteSpace(county)
        ? await service.GetFeedsAsync(state, ct)
        : await service.GetFeedsAsync(state, county, ct);

    var response = feeds.Select(feed =>
    {
        var feedId = feed.AudioSource.AbsolutePath.Trim('/');
        return new DiscoveryFeedDto(
            feed.State,
            feed.County,
            feed.FeedName,
            feed.FeedStatus,
            feedId,
            feed.AudioSource.ToString());
    });

    return Results.Ok(response);
});

app.MapGet("/api/local-audio/devices", async (ILocalAudioFeedProvider provider, CancellationToken ct) =>
{
    var devices = await provider.GetDevicesAsync(ct);
    var response = devices.Select(d => new LocalAudioDeviceDto(
        d.Id,
        d.Name,
        d.Backend,
        d.CaptureKind));
    return Results.Ok(response);
});

app.MapGet("/api/feeds", async (DispatchDbContext db, FeedCoordinator coordinator) =>
{
    var feeds = await db.Feeds.AsNoTracking()
        .Where(f => f.IsVisible)
        .OrderBy(f => f.Name)
        .ToListAsync();
    var response = feeds.Select(feed => new FeedDto(
        feed.Id,
        feed.Name,
        feed.BroadcastifyUrl,
        feed.StreamUrl,
        feed.FeedIdentifier,
        feed.IsActive,
        coordinator.IsRunning(feed.Id),
        AsUtc(feed.CreatedUtc),
        feed.LastStartedUtc.HasValue ? AsUtc(feed.LastStartedUtc.Value) : null,
        feed.LastStoppedUtc.HasValue ? AsUtc(feed.LastStoppedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapPost("/api/feeds", async (AddFeedRequest request, DispatchDbContext db, BroadcastifyResolver resolver) =>
{
    if (!resolver.TryResolve(request.BroadcastifyUrl, out var feedId, out var streamUrl))
    {
        return Results.BadRequest(new { message = "Unable to parse Broadcastify feed URL or ID." });
    }

    var name = string.IsNullOrWhiteSpace(request.Name) ? $"Feed {feedId}" : request.Name.Trim();
    var existingFeed = await db.Feeds.FirstOrDefaultAsync(f => f.FeedIdentifier == feedId);
    if (existingFeed != null)
    {
        existingFeed.Name = name;
        existingFeed.BroadcastifyUrl = request.BroadcastifyUrl.Trim();
        existingFeed.StreamUrl = streamUrl;
        existingFeed.IsVisible = true;
        await db.SaveChangesAsync();

        return Results.Ok(new FeedDto(
            existingFeed.Id,
            existingFeed.Name,
            existingFeed.BroadcastifyUrl,
            existingFeed.StreamUrl,
            existingFeed.FeedIdentifier,
            existingFeed.IsActive,
            false,
            AsUtc(existingFeed.CreatedUtc),
            existingFeed.LastStartedUtc.HasValue ? AsUtc(existingFeed.LastStartedUtc.Value) : null,
            existingFeed.LastStoppedUtc.HasValue ? AsUtc(existingFeed.LastStoppedUtc.Value) : null));
    }

    var feed = new Feed
    {
        Id = Guid.NewGuid(),
        Name = name,
        BroadcastifyUrl = request.BroadcastifyUrl.Trim(),
        StreamUrl = streamUrl,
        FeedIdentifier = feedId,
        IsVisible = true,
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

app.MapPost("/api/feeds/local", async (AddLocalFeedRequest request, DispatchDbContext db, ILocalAudioFeedProvider provider, FeedCoordinator coordinator, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.DeviceId))
    {
        return Results.BadRequest(new { message = "DeviceId is required." });
    }

    var devices = await provider.GetDevicesAsync(ct);
    var selectedDevice = devices.FirstOrDefault(d =>
        d.Id.Equals(request.DeviceId.Trim(), StringComparison.OrdinalIgnoreCase));
    if (selectedDevice == null)
    {
        return Results.BadRequest(new { message = "Selected local device was not found. Refresh devices and try again." });
    }

    var streamUrl = provider.BuildStreamUrl(selectedDevice);
    var feedIdentifier = provider.BuildFeedIdentifier(selectedDevice);
    var localSourceUrl = $"local://device/{Uri.EscapeDataString(selectedDevice.Id)}";
    var now = DateTime.UtcNow;
    var startImmediately = request.StartImmediately;
    var name = string.IsNullOrWhiteSpace(request.Name)
        ? $"{selectedDevice.Name} ({selectedDevice.CaptureKind})"
        : request.Name.Trim();

    var existingFeed = await db.Feeds.FirstOrDefaultAsync(f => f.FeedIdentifier == feedIdentifier, ct);
    Feed targetFeed;
    if (existingFeed != null)
    {
        existingFeed.Name = name;
        existingFeed.BroadcastifyUrl = localSourceUrl;
        existingFeed.StreamUrl = streamUrl;
        existingFeed.IsVisible = true;
        existingFeed.IsActive = startImmediately;
        if (startImmediately)
        {
            existingFeed.LastStartedUtc = now;
        }
        else
        {
            existingFeed.LastStoppedUtc = now;
        }

        targetFeed = existingFeed;
    }
    else
    {
        targetFeed = new Feed
        {
            Id = Guid.NewGuid(),
            Name = name,
            BroadcastifyUrl = localSourceUrl,
            StreamUrl = streamUrl,
            FeedIdentifier = feedIdentifier,
            IsVisible = true,
            IsActive = startImmediately,
            CreatedUtc = now,
            LastStartedUtc = startImmediately ? now : null,
            LastStoppedUtc = startImmediately ? null : now
        };

        db.Feeds.Add(targetFeed);
    }

    await db.SaveChangesAsync(ct);

    if (startImmediately)
    {
        await coordinator.StartAsync(targetFeed, ct, targetFeed.IsActive);
    }
    else if (coordinator.IsRunning(targetFeed.Id))
    {
        await coordinator.StopAsync(targetFeed.Id, targetFeed.IsActive);
    }

    return Results.Ok(new FeedDto(
        targetFeed.Id,
        targetFeed.Name,
        targetFeed.BroadcastifyUrl,
        targetFeed.StreamUrl,
        targetFeed.FeedIdentifier,
        targetFeed.IsActive,
        coordinator.IsRunning(targetFeed.Id),
        AsUtc(targetFeed.CreatedUtc),
        targetFeed.LastStartedUtc.HasValue ? AsUtc(targetFeed.LastStartedUtc.Value) : null,
        targetFeed.LastStoppedUtc.HasValue ? AsUtc(targetFeed.LastStoppedUtc.Value) : null));
});

app.MapPost("/api/feeds/{id:guid}/start", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id && f.IsVisible, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = true;
    feed.LastStartedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StartAsync(feed, ct, feed.IsActive);

    return Results.Ok();
});

app.MapPost("/api/feeds/{id:guid}/stop", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id && f.IsVisible, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsActive = false;
    feed.LastStoppedUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(ct);

    await coordinator.StopAsync(feed.Id, feed.IsActive);

    return Results.Ok();
});

app.MapGet("/api/feeds/{id:guid}/listen", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, IOptions<DecoderOptions> decoderOptions, HttpContext context, CancellationToken ct) =>
{
    var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.IsVisible, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    if (!coordinator.IsRunning(feed.Id))
    {
        return Results.BadRequest(new { message = "Feed is not currently running." });
    }

    var decode = decoderOptions.Value;
    var psi = new ProcessStartInfo
    {
        FileName = decode.FfmpegPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    psi.ArgumentList.Add("-hide_banner");
    psi.ArgumentList.Add("-loglevel");
    psi.ArgumentList.Add("error");

    var isLocalSource = LocalFeedUri.TryParse(feed.StreamUrl, out var localBackend, out var localInput);
    if (!isLocalSource && decode.EnableReconnect)
    {
        psi.ArgumentList.Add("-reconnect");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_streamed");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-reconnect_delay_max");
        psi.ArgumentList.Add(decode.ReconnectDelaySeconds.ToString());
    }

    if (isLocalSource)
    {
        var format = localBackend.ToLowerInvariant();
        switch (format)
        {
            case "avfoundation":
            case "dshow":
            case "pulse":
            case "alsa":
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add(format);
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(localInput);
                break;
            default:
                return Results.BadRequest(new { message = $"Unsupported local capture backend '{localBackend}'." });
        }
    }
    else
    {
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(feed.StreamUrl);
    }

    psi.ArgumentList.Add("-ac");
    psi.ArgumentList.Add(decode.Channels.ToString());
    psi.ArgumentList.Add("-ar");
    psi.ArgumentList.Add(decode.SampleRate.ToString());
    psi.ArgumentList.Add("-f");
    psi.ArgumentList.Add("wav");
    psi.ArgumentList.Add("pipe:1");

    using var process = Process.Start(psi);
    if (process == null)
    {
        return Results.Problem("Unable to start feed monitor process.");
    }

    var stderrTask = process.StandardError.ReadToEndAsync(ct);
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "audio/wav";

    try
    {
        await using var stream = process.StandardOutput.BaseStream;
        await stream.CopyToAsync(context.Response.Body, ct);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // expected on client disconnect
    }
    catch (IOException) when (ct.IsCancellationRequested)
    {
        // expected on client disconnect
    }
    finally
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    try
    {
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            app.Logger.LogDebug("Feed listen stderr for {FeedId}: {Stderr}", feed.FeedIdentifier, stderr);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        // ignore
    }

    return Results.Empty;
});

app.MapDelete("/api/feeds/{id:guid}", async (Guid id, DispatchDbContext db, FeedCoordinator coordinator, CancellationToken ct) =>
{
    var feed = await db.Feeds.FirstOrDefaultAsync(f => f.Id == id, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    feed.IsVisible = false;
    if (feed.IsActive)
    {
        feed.IsActive = false;
        feed.LastStoppedUtc = DateTime.UtcNow;
    }

    await db.SaveChangesAsync(ct);
    await coordinator.StopAsync(feed.Id, feed.IsActive);

    return Results.Ok();
});

app.MapGet("/api/feeds/{id:guid}/recordings/days", async (Guid id, bool includeArchived, DispatchDbContext db, CancellationToken ct) =>
{
    var feedExists = await db.Feeds.AsNoTracking()
        .AnyAsync(f => f.Id == id && f.IsVisible, ct);
    if (!feedExists)
    {
        return Results.NotFound();
    }

    var startTimes = await db.Recordings.AsNoTracking()
        .Where(r => r.FeedId == id && (includeArchived || !r.IsArchived))
        .Select(r => r.StartUtc)
        .ToListAsync(ct);

    var days = startTimes
        .Select(startUtc =>
        {
            var utc = AsUtc(startUtc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);
            return DateOnly.FromDateTime(local);
        })
        .GroupBy(day => day)
        .OrderByDescending(group => group.Key)
        .Select(group => new RecordingDaySummaryDto(
            group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            group.Count()))
        .ToList();

    return Results.Ok(days);
});

app.MapGet("/api/feeds/{id:guid}/recordings", async (Guid id, bool includeArchived, string? day, DateTimeOffset? since, DateTimeOffset? before, int? limit, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    var query = db.Recordings.AsNoTracking()
        .Where(r => r.FeedId == id);

    if (!includeArchived)
    {
        query = query.Where(r => !r.IsArchived);
    }

    if (!string.IsNullOrWhiteSpace(day))
    {
        if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate))
        {
            return Results.BadRequest(new { message = "Day must be in yyyy-MM-dd format." });
        }

        var localStart = dayDate.ToDateTime(TimeOnly.MinValue);
        var localEnd = localStart.AddDays(1);
        var utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Local));
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Local));
        query = query.Where(r => r.StartUtc >= utcStart && r.StartUtc < utcEnd);
    }

    if (since.HasValue)
    {
        var sinceUtc = since.Value.UtcDateTime;
        query = query.Where(r => r.StartUtc > sinceUtc);
    }

    if (before.HasValue)
    {
        var beforeUtc = before.Value.UtcDateTime;
        query = query.Where(r => r.StartUtc < beforeUtc);
    }

    var normalizedLimit = limit.HasValue && limit.Value > 0
        ? Math.Clamp(limit.Value, 1, 500)
        : 0;

    IQueryable<Recording> orderedQuery = query
        .OrderByDescending(r => r.StartUtc)
        .ThenByDescending(r => r.Id);

    if (normalizedLimit > 0)
    {
        orderedQuery = orderedQuery.Take(normalizedLimit);
    }

    var recordings = await orderedQuery.ToListAsync(ct);

    var queueInfo = await GetPendingQueueAsync(db, ct);

    var response = recordings.Select(r => new RecordingDto(
        r.Id,
        r.FeedId,
        r.FilePath,
        AsUtc(r.StartUtc),
        AsUtc(r.EndUtc),
        r.DurationSeconds,
        r.TranscriptStatus,
        ComputeTranscriptProgress(r, options.Value, GetRecordingFileSize(r, env)),
        queueInfo.Map.TryGetValue(r.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        r.TranscriptStartedUtc.HasValue ? AsUtc(r.TranscriptStartedUtc.Value) : null,
        r.TranscriptText,
        r.TranscriptPath,
        r.TranscriptProvider,
        r.IsArchived,
        r.ArchivedUtc.HasValue ? AsUtc(r.ArchivedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapGet("/api/feeds/{id:guid}/synthesis", async (Guid id, string day, bool includeArchived, DispatchDbContext db, IDailyTranscriptSynthesizer synthesizer, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(day) ||
        !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate))
    {
        return Results.BadRequest(new { message = "Day must be in yyyy-MM-dd format." });
    }

    var feed = await db.Feeds.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.IsVisible, ct);
    if (feed == null)
    {
        return Results.NotFound();
    }

    var localStart = dayDate.ToDateTime(TimeOnly.MinValue);
    var localEnd = localStart.AddDays(1);
    var utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Local));
    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Local));

    var query = db.Recordings.AsNoTracking()
        .Where(r => r.FeedId == id && r.StartUtc >= utcStart && r.StartUtc < utcEnd);

    if (!includeArchived)
    {
        query = query.Where(r => !r.IsArchived);
    }

    var recordings = await query
        .OrderBy(r => r.StartUtc)
        .ToListAsync(ct);

    var synthesis = synthesizer.Synthesize(recordings, feed.Name, dayDate);
    var response = new DailySynthesisDto(
        dayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        id,
        feed.Name,
        synthesis.TotalCalls,
        synthesis.TranscribedCalls,
        synthesis.Summary,
        synthesis.KeyThemes,
        synthesis.Categories
            .Select(c => new SynthesisCategoryDto(c.Category, c.Count))
            .ToList(),
        synthesis.Highlights
            .Select(h => new SynthesisHighlightDto(
                h.RecordingId,
                AsUtc(h.StartUtc),
                h.Category,
                h.Score,
                h.Excerpt))
            .ToList());

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

    var queueInfo = await GetPendingQueueAsync(db, ct);

    return Results.Ok(new RecordingDto(
        recording.Id,
        recording.FeedId,
        recording.FilePath,
        AsUtc(recording.StartUtc),
        AsUtc(recording.EndUtc),
        recording.DurationSeconds,
        recording.TranscriptStatus,
        ComputeTranscriptProgress(recording, options.Value, GetRecordingFileSize(recording, env)),
        queueInfo.Map.TryGetValue(recording.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        recording.TranscriptStartedUtc.HasValue ? AsUtc(recording.TranscriptStartedUtc.Value) : null,
        recording.TranscriptText,
        recording.TranscriptPath,
        recording.TranscriptProvider,
        recording.IsArchived,
        recording.ArchivedUtc.HasValue ? AsUtc(recording.ArchivedUtc.Value) : null));
});

app.MapPost("/api/recordings/batch", async (BatchRecordingsRequest request, DispatchDbContext db, IOptions<TranscriptionOptions> options, IWebHostEnvironment env, CancellationToken ct) =>
{
    if (request?.RecordingIds == null || request.RecordingIds.Count == 0)
    {
        return Results.Ok(Array.Empty<RecordingDto>());
    }

    var ids = request.RecordingIds
        .Where(id => id != Guid.Empty)
        .Distinct()
        .ToList();
    if (ids.Count == 0)
    {
        return Results.Ok(Array.Empty<RecordingDto>());
    }

    var recordings = await db.Recordings.AsNoTracking()
        .Where(r => ids.Contains(r.Id))
        .ToListAsync(ct);

    var queueInfo = await GetPendingQueueAsync(db, ct);

    var response = recordings.Select(r => new RecordingDto(
        r.Id,
        r.FeedId,
        r.FilePath,
        AsUtc(r.StartUtc),
        AsUtc(r.EndUtc),
        r.DurationSeconds,
        r.TranscriptStatus,
        ComputeTranscriptProgress(r, options.Value, GetRecordingFileSize(r, env)),
        queueInfo.Map.TryGetValue(r.Id, out var position) ? position : null,
        queueInfo.Total > 0 ? queueInfo.Total : null,
        r.TranscriptStartedUtc.HasValue ? AsUtc(r.TranscriptStartedUtc.Value) : null,
        r.TranscriptText,
        r.TranscriptPath,
        r.TranscriptProvider,
        r.IsArchived,
        r.ArchivedUtc.HasValue ? AsUtc(r.ArchivedUtc.Value) : null));

    return Results.Ok(response);
});

app.MapPost("/api/recordings/{id:guid}/reprocess", async (Guid id, DispatchDbContext db, IRecordingEventHub eventHub, CancellationToken ct) =>
{
    var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    if (!string.IsNullOrWhiteSpace(recording.TranscriptPath) && File.Exists(recording.TranscriptPath))
    {
        try
        {
            File.Delete(recording.TranscriptPath);
        }
        catch
        {
            // Ignore delete failures; reprocess will overwrite
        }
    }

    var fallbackTranscript = recording.FilePath + ".txt";
    if (File.Exists(fallbackTranscript))
    {
        try
        {
            File.Delete(fallbackTranscript);
        }
        catch
        {
            // Ignore delete failures
        }
    }

    recording.TranscriptStatus = TranscriptStatus.Pending;
    recording.TranscriptStartedUtc = null;
    recording.TranscribedUtc = null;
    recording.TranscriptText = null;
    recording.TranscriptPath = null;
    recording.TranscriptProvider = null;
    recording.Error = null;

    await db.SaveChangesAsync(ct);
    await eventHub.PublishAsync(new RecordingEvent(recording.Id, recording.FeedId, RecordingEventType.Updated));
    return Results.Ok();
});

app.MapPost("/api/recordings/{id:guid}/archive", async (Guid id, DispatchDbContext db, IRecordingEventHub eventHub, CancellationToken ct) =>
{
    var recording = await db.Recordings.FirstOrDefaultAsync(r => r.Id == id, ct);
    if (recording == null)
    {
        return Results.NotFound();
    }

    if (!recording.IsArchived)
    {
        recording.IsArchived = true;
        recording.ArchivedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await eventHub.PublishAsync(new RecordingEvent(recording.Id, recording.FeedId, RecordingEventType.Archived));
    }

    return Results.Ok();
});

app.MapPost("/api/feeds/{id:guid}/recordings/archive", async (Guid id, string day, DispatchDbContext db, IRecordingEventHub eventHub, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(day) ||
        !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dayDate))
    {
        return Results.BadRequest(new { message = "Day must be in yyyy-MM-dd format." });
    }

    var localStart = dayDate.ToDateTime(TimeOnly.MinValue);
    var localEnd = localStart.AddDays(1);
    var utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Local));
    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Local));

    var recordings = await db.Recordings
        .Where(r => r.FeedId == id && !r.IsArchived && r.StartUtc >= utcStart && r.StartUtc < utcEnd)
        .ToListAsync(ct);

    if (recordings.Count == 0)
    {
        return Results.Ok(new { archived = 0 });
    }

    var now = DateTime.UtcNow;
    foreach (var recording in recordings)
    {
        recording.IsArchived = true;
        recording.ArchivedUtc = now;
    }

    await db.SaveChangesAsync(ct);
    foreach (var recording in recordings)
    {
        await eventHub.PublishAsync(new RecordingEvent(recording.Id, recording.FeedId, RecordingEventType.Archived));
    }
    return Results.Ok(new { archived = recordings.Count });
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

app.MapGet("/api/ui-config", (IOptions<TranscriptionOptions> options) =>
{
    var opt = options.Value;
    return Results.Ok(new
    {
        expectedRealtimeFactor = opt.ExpectedRealtimeFactor,
        estimatedBytesPerSecond = opt.EstimatedBytesPerSecond
    });
});

app.MapGet("/api/feeds/stream", async (IFeedEventHub eventHub, DispatchDbContext db, FeedCoordinator coordinator, HttpContext context, CancellationToken ct) =>
{
    var subscription = eventHub.Subscribe(ct);

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    var feeds = await db.Feeds.AsNoTracking()
        .Where(f => f.IsVisible)
        .Select(f => new { f.Id, f.IsActive })
        .ToListAsync(ct);
    var snapshot = feeds.Select(f => new
    {
        feedId = f.Id,
        isRunning = coordinator.IsRunning(f.Id),
        isActive = f.IsActive
    });
    var snapshotPayload = JsonSerializer.Serialize(snapshot, jsonOptions);
    await context.Response.WriteAsync("event: snapshot\n", ct);
    await context.Response.WriteAsync($"data: {snapshotPayload}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);

    await foreach (var evt in subscription)
    {
        var payload = JsonSerializer.Serialize(evt, jsonOptions);
        await context.Response.WriteAsync("event: updated\n", ct);
        await context.Response.WriteAsync($"data: {payload}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

app.MapGet("/api/recordings/stream", async (Guid? feedId, DispatchDbContext db, IRecordingEventHub eventHub, HttpContext context, CancellationToken ct) =>
{
    var subscription = eventHub.Subscribe(ct);

    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    List<Guid> pendingIds;
    if (feedId.HasValue)
    {
        pendingIds = await db.Recordings.AsNoTracking()
            .Where(r => r.FeedId == feedId.Value &&
                        (r.TranscriptStatus == TranscriptStatus.Pending || r.TranscriptStatus == TranscriptStatus.Processing))
            .Select(r => r.Id)
            .ToListAsync(ct);
    }
    else
    {
        pendingIds = new List<Guid>();
    }

    var snapshotPayload = JsonSerializer.Serialize(new { feedId, recordingIds = pendingIds }, jsonOptions);
    await context.Response.WriteAsync("event: snapshot\n", ct);
    await context.Response.WriteAsync($"data: {snapshotPayload}\n\n", ct);
    await context.Response.Body.FlushAsync(ct);

    await foreach (var evt in subscription)
    {
        if (feedId.HasValue && evt.FeedId != feedId.Value)
        {
            continue;
        }

        var payload = JsonSerializer.Serialize(evt, jsonOptions);
        var eventName = evt.Type.ToString().ToLowerInvariant();
        await context.Response.WriteAsync($"event: {eventName}\n", ct);
        await context.Response.WriteAsync($"data: {payload}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});

app.MapFallbackToFile("index.html");

app.Run();
