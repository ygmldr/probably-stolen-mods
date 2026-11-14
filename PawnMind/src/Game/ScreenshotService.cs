using System;
using System.Collections;
using System.Collections.Generic;
using PawnMind.Core;
using UnityEngine;

namespace PawnMind.Game
{
    /// <summary>
    /// Main-thread JPEG evidence capture. Requests only touch a bounded queue;
    /// the actual screen read, downscale and PNG/JPEG encode happen at the end of
    /// the current frame in a coroutine. Every failure is swallowed and logged:
    /// evidence capture must never break gameplay or the bridge.
    /// </summary>
    public sealed class ScreenshotService : MonoBehaviour
    {
        public const string ModId = "pawnmind";
        public const string Folder = "screenshots";

        private ScreenshotLedger _ledger;
        private bool _running;

        public string LastPath { get; private set; }
        public string LastError { get; private set; }

        public ScreenshotLedger Ledger
        {
            get { return _ledger; }
        }

        public void Initialize()
        {
            if (_ledger != null) return;

            _ledger = new ScreenshotLedger(
                ReadUsage,
                delegate { return DateTime.UtcNow; },
                ScreenshotLedger.DefaultBudgetBytes,
                ScreenshotLedger.DefaultQueueCapacity);
        }

        public ScreenshotRequest Request(string op, string outcome, long rev, int day, long cash)
        {
            ScreenshotLedger ledger = _ledger;
            if (ledger == null)
            {
                return ScreenshotRequest.Reject("screenshot_unavailable");
            }

            ScreenshotRequest request;
            try
            {
                request = ledger.Request(op, outcome, rev, day, cash);
            }
            catch (Exception e)
            {
                LastError = "screenshot_failed";
                Debug.LogWarning("[PawnMind] screenshot request failed: " + e.GetType().Name);
                return ScreenshotRequest.Reject("screenshot_failed");
            }

            if (request.Accepted && !_running)
            {
                _running = true;
                StartCoroutine(CaptureLoop());
            }

            return request;
        }

        public IReadOnlyList<ScreenshotEntry> Recent(int limit)
        {
            ScreenshotLedger ledger = _ledger;
            return ledger == null
                ? new List<ScreenshotEntry>()
                : ledger.Recent(limit);
        }

        private void OnDestroy()
        {
            _ledger = null;
            _running = false;
        }

        private IEnumerator CaptureLoop()
        {
            while (true)
            {
                ScreenshotEntry entry;
                if (_ledger == null || !_ledger.TryDequeue(out entry))
                {
                    _running = false;
                    yield break;
                }

                yield return new WaitForEndOfFrame();

                try
                {
                    CaptureOne(entry);
                }
                catch (Exception e)
                {
                    LastError = "screenshot_failed";
                    if (_ledger != null) _ledger.MarkFailed(entry, LastError);
                    Debug.LogWarning("[PawnMind] screenshot failed: " + e.GetType().Name);
                }
            }
        }

        private void CaptureOne(ScreenshotEntry entry)
        {
            byte[] bytes;
            try
            {
                bytes = CaptureJpeg();
            }
            catch (Exception e)
            {
                LastError = "screenshot_failed";
                if (_ledger != null) _ledger.MarkFailed(entry, LastError);
                Debug.LogWarning("[PawnMind] screenshot capture failed: " + e.GetType().Name);
                return;
            }

            if (bytes == null || bytes.Length == 0)
            {
                LastError = "screenshot_empty";
                if (_ledger != null) _ledger.MarkFailed(entry, LastError);
                return;
            }

            try
            {
                ModHelper.WriteModDataFileBytes(ModId, entry.Path, bytes);
                LastPath = entry.Path;
                LastError = null;
                if (_ledger != null) _ledger.MarkCaptured(entry, bytes.LongLength);
            }
            catch (Exception e)
            {
                LastError = "screenshot_write_failed";
                if (_ledger != null) _ledger.MarkFailed(entry, LastError);
                Debug.LogWarning("[PawnMind] screenshot write failed: " + e.GetType().Name);
            }
        }

        private static byte[] CaptureJpeg()
        {
            Texture2D source = null;
            Texture2D output = null;
            RenderTexture target = null;
            RenderTexture previous = null;

            try
            {
                source = ScreenCapture.CaptureScreenshotAsTexture();
                if (source == null) return null;

                output = source;
                if (ScreenshotImage.NeedsScale(source.width, source.height))
                {
                    ScreenshotSize size = ScreenshotImage.ScaleForShortSide(
                        source.width, source.height);

                    target = RenderTexture.GetTemporary(
                        size.Width, size.Height, 0, RenderTextureFormat.Default);
                    Graphics.Blit(source, target);

                    previous = RenderTexture.active;
                    RenderTexture.active = target;

                    output = new Texture2D(size.Width, size.Height, TextureFormat.RGB24, false);
                    output.ReadPixels(new Rect(0, 0, size.Width, size.Height), 0, 0);
                    output.Apply();
                }

                return output.EncodeToJPG(ScreenshotImage.JpegQuality);
            }
            finally
            {
                if (previous != null) RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (output != null && !ReferenceEquals(output, source))
                {
                    UnityEngine.Object.Destroy(output);
                }
                if (source != null) UnityEngine.Object.Destroy(source);
            }
        }

        private static ScreenshotUsage ReadUsage()
        {
            try
            {
                string folder = System.IO.Path.Combine(ModHelper.GetModDataFolder(ModId), Folder);
                if (!System.IO.Directory.Exists(folder)) return new ScreenshotUsage(0, 0);

                int count = 0;
                long bytes = 0;
                string[] files = System.IO.Directory.GetFiles(folder, "*", System.IO.SearchOption.TopDirectoryOnly);
                for (int i = 0; i < files.Length; i++)
                {
                    count++;
                    try
                    {
                        bytes += new System.IO.FileInfo(files[i]).Length;
                    }
                    catch (Exception)
                    {
                    }
                }

                return new ScreenshotUsage(count, bytes);
            }
            catch (Exception)
            {
                return new ScreenshotUsage(0, 0);
            }
        }
    }
}
