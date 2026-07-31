namespace SmartLab.Fat;

/// <summary>
/// Reads arbitrary byte ranges from a device that only permits sector-aligned I/O.
/// </summary>
/// <remarks>
/// <para>
/// A volume opened as <c>\\.\E:</c> rejects any read whose offset or length is not
/// a multiple of the sector size, with ERROR_INVALID_PARAMETER ("The parameter is
/// incorrect"). A <see cref="MemoryStream"/> has no such restriction, so code that
/// reads a 4-byte FAT entry at its natural offset passes every in-memory test and
/// then fails on the first real device. This type removes that difference: callers
/// ask for the bytes they want and the alignment is handled here.
/// </para>
/// <para>
/// The single-sector cache is not an optimisation detail. Walking a cluster chain
/// reads consecutive 4-byte FAT entries, which nearly always fall in the same
/// sector; without the cache each step would issue a fresh 512-byte device read.
/// </para>
/// </remarks>
internal sealed class SectorReader(Stream stream, int sectorSize)
{
    private readonly byte[] _sector = new byte[sectorSize];
    private long _cachedSectorIndex = -1;

    /// <summary>
    /// Reads <paramref name="destination"/>.Length bytes from <paramref name="offset"/>.
    /// </summary>
    /// <returns>False if the range is unreadable or runs past the end of the device.</returns>
    public bool TryRead(long offset, Span<byte> destination)
    {
        if (offset < 0) return false;

        var written = 0;

        while (written < destination.Length)
        {
            var absolute = offset + written;
            var sectorIndex = absolute / sectorSize;
            var withinSector = (int)(absolute % sectorSize);

            if (!TryLoadSector(sectorIndex)) return false;

            var available = sectorSize - withinSector;
            var wanted = Math.Min(available, destination.Length - written);

            _sector.AsSpan(withinSector, wanted).CopyTo(destination[written..]);
            written += wanted;
        }

        return true;
    }

    private bool TryLoadSector(long sectorIndex)
    {
        if (sectorIndex == _cachedSectorIndex) return true;

        var offset = sectorIndex * sectorSize;

        try
        {
            stream.Seek(offset, SeekOrigin.Begin);

            var read = 0;
            while (read < sectorSize)
            {
                var n = stream.Read(_sector, read, sectorSize - read);
                if (n == 0) break;
                read += n;
            }

            if (read < sectorSize)
            {
                _cachedSectorIndex = -1;
                return false;
            }
        }
        catch (IOException)
        {
            // A bad sector on a failing device must cost that sector, not the run.
            _cachedSectorIndex = -1;
            return false;
        }

        _cachedSectorIndex = sectorIndex;
        return true;
    }
}
