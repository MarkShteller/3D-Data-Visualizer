using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PointCloud.Core.Diagnostics;

namespace PointCloud.Core.Sources
{
    /// <summary>Outcome of one load attempt. Failure is a value, not only an exception.</summary>
    public sealed class LoadResult
    {
        public PointCloudFrame Frame;
        public string          SourcePath;
        public bool            Cancelled;
        public Exception       Error;
        public double          ElapsedMs;

        public bool Succeeded => Frame != null && Error == null && !Cancelled;

        /// <summary>
        /// A message fit to show the user. Format exceptions already carry the byte offset
        /// and the reason, so they are surfaced verbatim rather than wrapped in noise.
        /// </summary>
        public string UserMessage => Cancelled
            ? "Load cancelled."
            : Error switch
            {
                null                          => $"Loaded in {ElapsedMs:F0} ms",
                PointCloudFormatException f   => f.Message,
                System.IO.FileNotFoundException => $"File not found: {SourcePath}",
                System.IO.IOException io      => $"Could not read the file: {io.Message}",
                _                             => Error.Message,
            };
    }

    /// <summary>
    /// Runs one load from path to decoded frame, reporting progress and honouring
    /// cancellation.
    ///
    /// Cancellation matters more than it looks: a user who picks the wrong multi-gigabyte
    /// file must be able to back out, and every native allocation made so far has to go
    /// back. Domain reload is disabled in this project, so a leak here survives into the
    /// next play session.
    /// </summary>
    public sealed class PointCloudLoader
    {
        readonly SourceRegistry _registry;
        readonly LoadLog        _log;

        public PointCloudLoader(SourceRegistry registry, LoadLog log = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _log = log;
        }

        public async Task<LoadResult> LoadAsync(string path, FrameRequest request,
                                                IProgress<LoadProgress> progress = null,
                                                CancellationToken cancellationToken = default)
        {
            var result = new LoadResult { SourcePath = path };
            var stopwatch = Stopwatch.StartNew();

            IPointCloudSource source = null;
            try
            {
                source = _registry.Create(path);
                _log?.Info(source.Id.ToUpperInvariant(), $"opening {source.DisplayName}");

                await source.OpenAsync(progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                result.Frame = await source.ReadFrameAsync(0, request, progress, cancellationToken)
                                           .ConfigureAwait(false);

                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                _log?.Info(source.Id.ToUpperInvariant(),
                           $"{source.DisplayName}: {result.Frame.Data.Descriptor} in {result.ElapsedMs:F0} ms");
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                _log?.Warning("Load", $"cancelled after {result.ElapsedMs:F0} ms: {path}");
            }
            catch (Exception e)
            {
                result.Error = e;
                result.ElapsedMs = stopwatch.Elapsed.TotalMilliseconds;
                _log?.Error("Load", result.UserMessage);
            }
            finally
            {
                if (source != null)
                {
                    try { await source.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception e) { _log?.Warning("Load", $"source dispose failed: {e.Message}"); }
                }
            }

            return result;
        }
    }
}
