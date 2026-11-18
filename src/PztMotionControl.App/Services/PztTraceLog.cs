using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PztMotionControl.App.Services;

// A single background writer keeps disk I/O off the UI and device-command paths.
internal sealed class PztTraceLog
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};
	private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(20000)
	{
		SingleReader = true,
		FullMode = BoundedChannelFullMode.Wait
	});
	private string? _lastError;
	private long _sequence;
	private int _hasWritten;
	private readonly string _sessionId = Guid.NewGuid().ToString("N");

	private static readonly Lazy<PztTraceLog> Shared = new(() => new PztTraceLog());
	public static PztTraceLog Instance => Shared.Value;
	public string DirectoryPath { get; }
	public string? LastError => Volatile.Read(ref _lastError);
	public bool HasWritten => Volatile.Read(ref _hasWritten) != 0;

	internal PztTraceLog(string? directoryPath = null)
	{
		DirectoryPath = directoryPath ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"NeedleInspection", "Logs", "PztMotion");
		_ = Task.Run(WriteLoopAsync);
		Write("logger_started", null, new { FormatVersion = 1 });
	}

	public void Write(string kind, string? operationId, object? data)
	{
		try
		{
			string line = JsonSerializer.Serialize(new
			{
				Time = DateTimeOffset.Now,
				SessionId = _sessionId,
				Sequence = Interlocked.Increment(ref _sequence),
				OperationId = operationId,
				Kind = kind,
				Data = data
			}, JsonOptions);
			if (!_pending.Writer.TryWrite(line))
				Interlocked.CompareExchange(ref _lastError, "日志队列已满，部分记录未保存。", null);
		}
		catch (Exception ex)
		{
			Interlocked.CompareExchange(ref _lastError, $"日志记录失败：{ex.Message}", null);
		}
	}

	private async Task WriteLoopAsync()
	{
		await foreach (string line in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			try
			{
				Directory.CreateDirectory(DirectoryPath);
				// The event timestamp, rather than write time, chooses the daily file.
				using JsonDocument document = JsonDocument.Parse(line);
				DateTimeOffset captured = document.RootElement.GetProperty("Time").GetDateTimeOffset();
				string path = Path.Combine(DirectoryPath, $"{captured:yyyy-MM-dd}.jsonl");
				byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
				using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
					4096, FileOptions.WriteThrough);
				await stream.WriteAsync(bytes).ConfigureAwait(false);
				stream.Flush(flushToDisk: true);
				Volatile.Write(ref _hasWritten, 1);
			}
			catch (Exception ex)
			{
				Interlocked.CompareExchange(ref _lastError, $"日志写入失败，部分记录未保存：{ex.Message}", null);
			}
		}
	}
}
