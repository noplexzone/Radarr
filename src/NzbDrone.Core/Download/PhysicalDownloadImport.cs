using System.Collections.Generic;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.MovieImport;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Download
{
    public sealed record PhysicalDownloadImportEnvelope(TrackedDownloadKey Key, RemoteMovie RemoteMovie, DownloadClientItem ImportItem, bool ShouldImport = true);

    public sealed record PhysicalDownloadImportResult(TrackedDownloadKey Key, List<ImportResult> ImportResults);
}
