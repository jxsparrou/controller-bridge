using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class Program
{
    internal const int MaxVdfFileBytes = 32 * 1024 * 1024;
    internal const int MaxVdfStringBytes = 1024 * 1024;
    internal const int MaxVdfDepth = 64;
    internal const int MaxVdfElements = 100000;
    private static readonly UTF8Encoding VdfUtf8 = new UTF8Encoding(false, true);

    // === VdfElement class ===
    public class VdfElement
    {
        public byte Type { get; set; } // 0x00: Map, 0x01: String, 0x02: Int32, 0x08: End
        public string Name { get; set; } = "";
        public string StringValue { get; set; } = "";
        public int IntValue { get; set; }
        public List<VdfElement> Children { get; set; }

        public VdfElement()
        {
            Children = new List<VdfElement>();
        }
    }

    // === VDF Read/Write methods ===
    public static VdfElement ParseShortcuts(byte[] bytes)
    {
        if (bytes.Length > MaxVdfFileBytes)
            throw new InvalidDataException("Shortcut file exceeds the 32 MiB limit.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        if (reader.ReadByte() != 0x00)
            throw new InvalidDataException("Expected a binary VDF root map.");
        string name = ReadNullTerminatedString(reader);
        if (name != "shortcuts")
            throw new InvalidDataException("Expected the shortcuts root map.");
        var root = ReadMap(reader, name);
        // Steam files may end at the root close or include the extra root marker.
        // Saves keep the established two-marker format; arbitrary trailing data
        // must never be silently discarded during editing.
        if (stream.Position < stream.Length && reader.ReadByte() != 0x08)
            throw new InvalidDataException("Invalid shortcut root trailer.");
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected data after shortcut root.");
        return root;
    }

    public static byte[] SerializeShortcuts(VdfElement root)
    {
        if (root.Type != 0x00 || root.Name != "shortcuts")
            throw new InvalidDataException("Expected the shortcuts root map.");
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
            writer.Write(root.Type);
            WriteNullTerminatedString(writer, root.Name);
            WriteMapContents(writer, root);
            writer.Write((byte)0x08); // Close root map
            writer.Write((byte)0x08); // Extra Steam trailing 0x08
            return ms.ToArray();
        }
    }

    public static VdfElement ReadMap(BinaryReader reader, string name)
    {
        int elements = 0;
        return ReadMap(reader, name, 0, ref elements);
    }

    private static VdfElement ReadMap(BinaryReader reader, string name, int depth, ref int elements)
    {
        if (depth > MaxVdfDepth)
            throw new InvalidDataException("VDF nesting exceeds the limit.");
        var element = new VdfElement();
        element.Type = 0x00;
        element.Name = name;
        while (true)
        {
            byte type = reader.ReadByte();
            if (type == 0x08)
            {
                break; // End of map
            }
            if (type != 0x00 && type != 0x01 && type != 0x02)
                throw new InvalidDataException("Unsupported VDF type: 0x" + type.ToString("X2"));
            if (++elements > MaxVdfElements)
                throw new InvalidDataException("VDF element count exceeds the limit.");
            string key = ReadNullTerminatedString(reader);
            if (type == 0x01)
            {
                string val = ReadNullTerminatedString(reader);
                var child = new VdfElement();
                child.Type = 0x01;
                child.Name = key;
                child.StringValue = val;
                element.Children.Add(child);
            }
            else if (type == 0x02)
            {
                int val = reader.ReadInt32();
                var child = new VdfElement();
                child.Type = 0x02;
                child.Name = key;
                child.IntValue = val;
                element.Children.Add(child);
            }
            else if (type == 0x00)
            {
                element.Children.Add(ReadMap(reader, key, depth + 1, ref elements));
            }
        }
        return element;
    }

    public static string ReadNullTerminatedString(BinaryReader reader)
    {
        List<byte> bytes = new List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0)
                break;
            if (bytes.Count >= MaxVdfStringBytes)
                throw new InvalidDataException("VDF string exceeds the 1 MiB limit.");
            bytes.Add(b);
        }
        try
        {
            return VdfUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("VDF contains invalid UTF-8.", ex);
        }
    }

    public static void WriteNullTerminatedString(BinaryWriter writer, string str)
    {
        if (str == null || str.Contains('\0'))
            throw new InvalidDataException("VDF strings cannot be null or contain NUL.");
        byte[] bytes;
        try
        {
            if (VdfUtf8.GetByteCount(str) > MaxVdfStringBytes)
                throw new InvalidDataException("VDF string exceeds the 1 MiB limit.");
            bytes = VdfUtf8.GetBytes(str);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException("VDF string contains invalid Unicode.", ex);
        }
        writer.Write(bytes);
        writer.Write((byte)0x00);
    }

    public static void WriteMapContents(BinaryWriter writer, VdfElement element)
    {
        int elements = 0;
        WriteMapContents(writer, element, 0, ref elements);
    }

    private static void WriteMapContents(BinaryWriter writer, VdfElement element, int depth, ref int elements)
    {
        if (depth > MaxVdfDepth)
            throw new InvalidDataException("VDF nesting exceeds the limit (or contains a cycle).");
        if (element.Children == null)
            throw new InvalidDataException("VDF map children cannot be null.");
        foreach (var child in element.Children)
        {
            if (child == null || (child.Type != 0x00 && child.Type != 0x01 && child.Type != 0x02))
                throw new InvalidDataException("Unsupported VDF element type.");
            if (++elements > MaxVdfElements)
                throw new InvalidDataException("VDF element count exceeds the limit.");
            writer.Write(child.Type);
            WriteNullTerminatedString(writer, child.Name);
            if (child.Type == 0x01)
            {
                WriteNullTerminatedString(writer, child.StringValue);
            }
            else if (child.Type == 0x02)
            {
                writer.Write(child.IntValue);
            }
            else if (child.Type == 0x00)
            {
                WriteMapContents(writer, child, depth + 1, ref elements);
                writer.Write((byte)0x08); // Close nested map
            }
            if (writer.BaseStream.Position > MaxVdfFileBytes - 2)
                throw new InvalidDataException("Shortcut file exceeds the 32 MiB limit.");
        }
    }
}
