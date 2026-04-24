// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using osu.Framework.Logging;
using osu.Game.Database;
using Uri = Android.Net.Uri;

namespace osu.Android
{
    public class AndroidImportTask : ImportTask
    {
        private readonly ContentResolver contentResolver;

        private readonly Uri uri;

        private AndroidImportTask(Stream stream, string filename, ContentResolver contentResolver, Uri uri)
            : base(stream, filename)
        {
            this.contentResolver = contentResolver;
            this.uri = uri;
        }

        public override void DeleteFile()
        {
            try
            {
                contentResolver.Delete(uri, null, null);
            }
            catch (Java.Lang.SecurityException e)
            {
                // Some third-party file managers (notably MIUI's `com.android.fileexplorer`)
                // share content URIs without granting the receiving app write permission, so
                // the post-import source-file delete throws SecurityException. The import
                // itself has already succeeded by this point; failing to delete the original
                // is a best-effort cleanup, so swallow the exception and log at info level
                // instead of letting it surface as a user-facing red error notification.
                Logger.Log($"Skipped deleting imported source file (provider denied write access): {e.Message}", LoggingTarget.Database);
            }
            catch (Exception e)
            {
                Logger.Log($"Failed to delete imported source file: {e.Message}", LoggingTarget.Database);
            }
        }

        public static async Task<AndroidImportTask?> Create(ContentResolver contentResolver, Uri uri)
        {
            // there are more performant overloads of this method, but this one is the most backwards-compatible
            // (dates back to API 1).
            string filename;

            using (var cursor = contentResolver.Query(uri, null, null, null, null))
            {
                if (cursor == null)
                    return null;

                if (!cursor.MoveToFirst())
                    return null;

                int filenameColumn = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                filename = cursor.GetString(filenameColumn) ?? uri.Path ?? string.Empty;
            }

            // SharpCompress requires archive streams to be seekable, which the stream opened by
            // OpenInputStream() seems to not necessarily be.
            // copy to an arbitrary-access memory stream to be able to proceed with the import.
            var copy = new MemoryStream();

            using (var stream = contentResolver.OpenInputStream(uri))
            {
                if (stream == null)
                {
                    copy.Dispose();
                    return null;
                }

                await stream.CopyToAsync(copy).ConfigureAwait(false);
            }

            return new AndroidImportTask(copy, filename, contentResolver, uri);
        }
    }
}
