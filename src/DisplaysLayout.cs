using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AgentSleep
{
    internal sealed class DisplayLayout
    {
        internal string Id;
        internal DisplayKind Kind;
        internal bool VirtualMode, BoostRefresh;
        internal int Rotation, Scaling, Scan;
        internal Ccd.Ratio Refresh;
        internal Ccd.SourceMode Source;
        internal Ccd.Signal Signal;
        internal Ccd.DesktopImage? Desktop;
        internal bool DesktopUsesSource;

        internal bool Origin { get { return Source.X == 0 && Source.Y == 0; } }

        internal DisplayLayout AtPosition(int x, int y)
        {
            var copy = (DisplayLayout)MemberwiseClone();
            copy.Source.X = x;
            copy.Source.Y = y;
            return copy;
        }

        internal bool Same(DisplayLayout other)
        {
            return other != null && Id == other.Id && Kind == other.Kind && Source.Equals(other.Source) &&
                VirtualMode == other.VirtualMode && BoostRefresh == other.BoostRefresh &&
                Rotation == other.Rotation && Scaling == other.Scaling && Scan == other.Scan &&
                Refresh.Same(other.Refresh) && Signal.PixelRate == other.Signal.PixelRate &&
                Signal.HSync.Same(other.Signal.HSync) && Signal.VSync.Same(other.Signal.VSync) &&
                Signal.Width == other.Signal.Width && Signal.Height == other.Signal.Height &&
                Signal.TotalWidth == other.Signal.TotalWidth && Signal.TotalHeight == other.Signal.TotalHeight &&
                Signal.AdditionalInfo == other.Signal.AdditionalInfo && Signal.Scan == other.Signal.Scan &&
                Desktop.Equals(other.Desktop) && DesktopUsesSource == other.DesktopUsesSource;
        }

        internal static bool Exact(DisplayLayout[] expected, DisplayLayout[] actual)
        {
            return expected.Length == actual.Length && expected.All(e => actual.Count(e.Same) == 1);
        }
    }

    internal static class DisplayBaseline
    {
        private const uint MagicV1 = 0x31445341, MagicV2 = 0x32445341; // ASD1, ASD2
        private const int MaxDisplays = 32, MaxBytes = 65536;

        internal static bool Usable(Ccd.SourceMode source)
        {
            return Dimensions(source.Width, source.Height) && source.Format >= 1 && source.Format <= 4 &&
                Math.Abs((long)source.X) <= 1000000 && Math.Abs((long)source.Y) <= 1000000;
        }

        private static bool Dimensions(uint width, uint height)
        {
            return width > 0 && height > 0 && width <= 65535 && height <= 65535;
        }

        private static bool ValidRate(Ccd.Ratio value)
        {
            return value.Denominator != 0 || value.Numerator == 0;
        }

        private static void Verify(DisplayLayout[] records)
        {
            if (records.Length < 1 || records.Length > MaxDisplays || records.Count(r => r.Origin) != 1 ||
                !records.Any(r => r.Kind == DisplayKind.Physical) || records.Select(r => r.Id).Distinct().Count() != records.Length ||
                records.GroupBy(r => new { r.Source.X, r.Source.Y }).Any(g => g.Count() != 1) ||
                records.Any(r => r.Id == null || !Regex.IsMatch(r.Id, @"\A[0-9A-F]{64}\z") ||
                    (r.Kind != DisplayKind.Physical && r.Kind != DisplayKind.Virtual) || !Usable(r.Source) ||
                    r.Rotation < 1 || r.Rotation > 4 || !(r.Scaling >= 1 && r.Scaling <= 5 || r.Scaling == 128) ||
                    r.Scan < 0 || r.Scan > 3 || !ValidRate(r.Refresh) ||
                    !Dimensions(r.Signal.Width, r.Signal.Height) || !ValidRate(r.Signal.HSync) ||
                    !ValidRate(r.Signal.VSync) || r.Desktop.HasValue && !r.VirtualMode ||
                    r.DesktopUsesSource && !r.Desktop.HasValue))
                throw new InvalidDataException("The display layout needs a known physical screen and one unambiguous primary. Use separate, supported displays in Windows and retry.");
        }

        internal static byte[] Encode(DisplayLayout[] records, string selectedId = null)
        {
            Verify(records);
            VerifySelected(records, selectedId);
            using (var buffer = new MemoryStream())
            using (var output = new BinaryWriter(buffer))
            {
                output.Write(selectedId == null ? MagicV1 : MagicV2);
                if (selectedId != null) output.Write(selectedId);
                output.Write(records.Length);
                foreach (DisplayLayout record in records)
                {
                    output.Write(record.Id);
                    output.Write((byte)record.Kind);
                    output.Write(record.VirtualMode);
                    output.Write(record.BoostRefresh);
                    output.Write(record.Rotation);
                    output.Write(record.Scaling);
                    output.Write(record.Scan);
                    output.Write(record.Refresh.Numerator);
                    output.Write(record.Refresh.Denominator);
                    WriteMode(output, new Ccd.Mode { Source = record.Source });
                    WriteMode(output, new Ccd.Mode { Signal = record.Signal });
                    output.Write(record.Desktop.HasValue);
                    if (record.Desktop.HasValue)
                    {
                        output.Write(record.DesktopUsesSource);
                        WriteMode(output, new Ccd.Mode { Desktop = record.Desktop.Value });
                    }
                }
                return buffer.ToArray();
            }
        }

        internal static DisplayLayout[] Decode(byte[] data)
        {
            string selectedId;
            return Decode(data, out selectedId);
        }

        internal static DisplayLayout[] Decode(byte[] data, out string selectedId)
        {
            selectedId = null;
            if (data == null || data.Length > MaxBytes)
                throw new InvalidDataException("The saved Windows display layout is missing or invalid.");
            try
            {
                using (var input = new BinaryReader(new MemoryStream(data, false)))
                {
                    uint magic = input.ReadUInt32();
                    if (magic != MagicV1 && magic != MagicV2)
                        throw new InvalidDataException("Unrecognized saved Windows display layout version.");
                    if (magic == MagicV2) selectedId = ReadIdentity(input);
                    int count = input.ReadInt32();
                    if (count < 1 || count > MaxDisplays)
                        throw new InvalidDataException("Invalid saved display count.");
                    var records = new DisplayLayout[count];
                    for (int index = 0; index < count; index++)
                    {
                        var record = new DisplayLayout
                        {
                            Id = ReadIdentity(input),
                            Kind = (DisplayKind)input.ReadByte(),
                            VirtualMode = ReadBoolean(input),
                            BoostRefresh = ReadBoolean(input),
                            Rotation = input.ReadInt32(),
                            Scaling = input.ReadInt32(),
                            Scan = input.ReadInt32(),
                            Refresh = new Ccd.Ratio { Numerator = input.ReadUInt32(), Denominator = input.ReadUInt32() },
                            Source = ReadMode(input).Source,
                            Signal = ReadMode(input).Signal
                        };
                        if (ReadBoolean(input))
                        {
                            record.DesktopUsesSource = ReadBoolean(input);
                            record.Desktop = ReadMode(input).Desktop;
                        }
                        records[index] = record;
                    }
                    if (input.BaseStream.Position != input.BaseStream.Length)
                        throw new InvalidDataException("Unexpected data in the saved Windows display layout.");
                    Verify(records);
                    VerifySelected(records, selectedId);
                    return records;
                }
            }
            catch (EndOfStreamException)
            {
                throw new InvalidDataException("The saved Windows display layout is incomplete.");
            }
        }

        private static void VerifySelected(DisplayLayout[] records, string selectedId)
        {
            if (selectedId == null) return;
            if (!Regex.IsMatch(selectedId, @"\A[0-9A-F]{64}\z") ||
                records.Any(r => r.Id == selectedId && r.Kind != DisplayKind.Virtual))
                throw new InvalidDataException("The saved selected virtual display identity is invalid.");
            if (records.Any(r => r.Id == selectedId && r.Origin))
                throw new InvalidDataException("Choose a different primary display in Windows before starting AgentSleep. The selected virtual display is detached after recovery.");
        }

        private static string ReadIdentity(BinaryReader input)
        {
            if (input.ReadByte() != 64) throw new InvalidDataException("Invalid saved display identity.");
            byte[] bytes = input.ReadBytes(64);
            if (bytes.Length != 64) throw new EndOfStreamException();
            return System.Text.Encoding.ASCII.GetString(bytes);
        }

        private static bool ReadBoolean(BinaryReader input)
        {
            byte value = input.ReadByte();
            if (value > 1) throw new InvalidDataException("Invalid flag in the saved Windows display layout.");
            return value != 0;
        }

        private static void WriteMode(BinaryWriter output, Ccd.Mode mode)
        {
            output.Write(mode.Data0);
            output.Write(mode.Data1);
            output.Write(mode.Data2);
            output.Write(mode.Data3);
            output.Write(mode.Data4);
            output.Write(mode.Data5);
        }

        private static Ccd.Mode ReadMode(BinaryReader input)
        {
            return new Ccd.Mode
            {
                Data0 = input.ReadUInt64(),
                Data1 = input.ReadUInt64(),
                Data2 = input.ReadUInt64(),
                Data3 = input.ReadUInt64(),
                Data4 = input.ReadUInt64(),
                Data5 = input.ReadUInt64()
            };
        }
    }
}
