using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Movies
{
    public enum MovieAcquisitionTargetKind
    {
        Unknown = 0,
        Main = 1,
        EditionSlot = 2
    }

    public sealed class MovieAcquisitionTarget : IEquatable<MovieAcquisitionTarget>
    {
        public static MovieAcquisitionTarget Unknown { get; } = new MovieAcquisitionTarget(MovieAcquisitionTargetKind.Unknown, null);
        public static MovieAcquisitionTarget Main { get; } = new MovieAcquisitionTarget(MovieAcquisitionTargetKind.Main, null);

        [JsonConstructor]
        public MovieAcquisitionTarget(MovieAcquisitionTargetKind kind, int? editionSlotId)
        {
            if (kind == MovieAcquisitionTargetKind.EditionSlot)
            {
                if (!editionSlotId.HasValue || editionSlotId.Value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(editionSlotId), editionSlotId, "Edition slot targets require a positive slot ID.");
                }
            }
            else if (kind != MovieAcquisitionTargetKind.Main && kind != MovieAcquisitionTargetKind.Unknown)
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported acquisition target kind.");
            }
            else if (editionSlotId.HasValue)
            {
                throw new ArgumentException("Only edition slot targets may carry a slot ID.", nameof(editionSlotId));
            }

            Kind = kind;
            EditionSlotId = editionSlotId;
        }

        public MovieAcquisitionTargetKind Kind { get; }
        public int? EditionSlotId { get; }

        public static MovieAcquisitionTarget ForEditionSlot(int editionSlotId)
        {
            if (editionSlotId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(editionSlotId), editionSlotId, "Edition slot IDs must be positive.");
            }

            return new MovieAcquisitionTarget(MovieAcquisitionTargetKind.EditionSlot, editionSlotId);
        }

        public bool Equals(MovieAcquisitionTarget other)
        {
            return other != null && Kind == other.Kind && EditionSlotId == other.EditionSlotId;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as MovieAcquisitionTarget);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)Kind, EditionSlotId);
        }

        public override string ToString()
        {
            return Kind == MovieAcquisitionTargetKind.EditionSlot ? $"EditionSlot:{EditionSlotId}" : Kind.ToString();
        }
    }

    public static class MovieAcquisitionTargetSerializer
    {
        private const string MainValue = "main";
        private const string EditionSlotValue = "editionSlot";
        private const string UnknownValue = "unknown";

        public static void Write(IDictionary<string, string> data, MovieAcquisitionTarget target)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            data.Remove(MovieHistory.MOVIE_EDITION_SLOT_ID);

            switch (target.Kind)
            {
                case MovieAcquisitionTargetKind.Main:
                    data[MovieHistory.ACQUISITION_TARGET] = MainValue;
                    break;
                case MovieAcquisitionTargetKind.EditionSlot:
                    data[MovieHistory.ACQUISITION_TARGET] = EditionSlotValue;
                    data[MovieHistory.MOVIE_EDITION_SLOT_ID] = target.EditionSlotId.Value.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    data[MovieHistory.ACQUISITION_TARGET] = UnknownValue;
                    break;
            }
        }

        public static MovieAcquisitionTarget Read(IReadOnlyDictionary<string, string> data)
        {
            if (data == null || !data.TryGetValue(MovieHistory.ACQUISITION_TARGET, out var kind))
            {
                return MovieAcquisitionTarget.Unknown;
            }

            if (string.Equals(kind, MainValue, StringComparison.OrdinalIgnoreCase))
            {
                return MovieAcquisitionTarget.Main;
            }

            if (string.Equals(kind, UnknownValue, StringComparison.OrdinalIgnoreCase))
            {
                return MovieAcquisitionTarget.Unknown;
            }

            return string.Equals(kind, EditionSlotValue, StringComparison.OrdinalIgnoreCase) && TryReadSlot(data, out var target)
                ? target
                : MovieAcquisitionTarget.Unknown;
        }

        // Compatibility boundary for histories written before acquisitionTarget existed.
        // Missing identity means Main only here; all ordinary reconstruction uses Read and fails closed.
        public static MovieAcquisitionTarget ReadLegacyHistory(IReadOnlyDictionary<string, string> data)
        {
            if (data != null && data.ContainsKey(MovieHistory.ACQUISITION_TARGET))
            {
                return Read(data);
            }

            if (data != null && data.ContainsKey(MovieHistory.MOVIE_EDITION_SLOT_ID))
            {
                return TryReadSlot(data, out var target) ? target : MovieAcquisitionTarget.Unknown;
            }

            return MovieAcquisitionTarget.Main;
        }

        private static bool TryReadSlot(IReadOnlyDictionary<string, string> data, out MovieAcquisitionTarget target)
        {
            target = MovieAcquisitionTarget.Unknown;
            if (!data.TryGetValue(MovieHistory.MOVIE_EDITION_SLOT_ID, out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var slotId) || slotId <= 0)
            {
                return false;
            }

            target = MovieAcquisitionTarget.ForEditionSlot(slotId);
            return true;
        }
    }
}
