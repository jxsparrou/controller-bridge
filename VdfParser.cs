using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class Program
{
    // === VdfElement class ===
    public class VdfElement
    {
        public byte Type { get; set; } // 0x00: Map, 0x01: String, 0x02: Int32, 0x08: End
        public string Name { get; set; }
        public string StringValue { get; set; }
        public int IntValue { get; set; }
        public List<VdfElement> Children { get; set; }

        public VdfElement()
        {
            Children = new List<VdfElement>();
        }
    }

    // === VDF Read/Write methods ===
    public static VdfElement ReadMap(BinaryReader reader, string name)
    {
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
                element.Children.Add(ReadMap(reader, key));
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
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public static void WriteNullTerminatedString(BinaryWriter writer, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        writer.Write(bytes);
        writer.Write((byte)0x00);
    }

    public static void WriteMapContents(BinaryWriter writer, VdfElement element)
    {
        foreach (var child in element.Children)
        {
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
                WriteMapContents(writer, child);
                writer.Write((byte)0x08); // Close nested map
            }
        }
    }
}
