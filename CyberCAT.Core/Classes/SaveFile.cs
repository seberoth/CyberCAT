﻿using CyberCAT.Core.Classes.Interfaces;
using CyberCAT.Core.Classes.Parsers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CyberCAT.Core.Classes.Mapping;

namespace CyberCAT.Core.Classes
{
    public class SaveFile
    {
        public class SaveFileHeader
        {
            public byte[] Magic { get; set; }
            public uint SaveVersion { get; set; }
            public uint GameVersion { get; set; }
            public byte Padding { get; set; }
            public uint Clock { get; set; }
            public uint Date { get; set; }
            public uint ArchiveVersion { get; set; }

            public byte Hour => (byte) (Clock >> 22);
            public byte Minutes => (byte) ((Clock >> 16) & 63);
            public byte Seconds => (byte) ((Clock >> 10) & 63);
            public byte Millis => (byte) (Clock & 1023);

            public short Year => (short) (Date >> 20);
            public byte Month => (byte) (1 + (Date >> 15) % (1 << 5));
            public byte Day => (byte)(1 + (Date >> 10) % (1 << 5));

            public void ReadSaveFileHeader(BinaryReader reader)
            {
                Magic = reader.ReadBytes(4);
                SaveVersion = reader.ReadUInt32();
                GameVersion = reader.ReadUInt32();
                Padding = reader.ReadByte();
                Clock = reader.ReadUInt32();
                Date = reader.ReadUInt32();
                ArchiveVersion = reader.ReadUInt32();
            }

            public void Write(BinaryWriter writer)
            {
                writer.Write(Magic);
                writer.Write(SaveVersion);
                writer.Write(GameVersion);
                writer.Write(Padding);
                writer.Write(Clock);
                writer.Write(Date);
                writer.Write(ArchiveVersion);
            }

            public override string ToString()
            {
                return $"v{SaveVersion}_{GameVersion}_{ArchiveVersion} saved at {Year}-{Month}-{Day} {Hour}:{Minutes}:{Seconds}.{Millis}";
            }
        }

        public SaveFileHeader Header { get; set; }
        public List<NodeEntry> Nodes;
        public List<NodeEntry> FlatNodes; //flat structure
        public Guid Guid { get; }
        private readonly List<INodeParser> _parsers;

        public enum ParserList
        {
            Simple,
            Enhanced,
            All
        }

        public SaveFile() : this(ParserList.All) { }

        public SaveFile(ParserList parsers)
        {
            _parsers = new List<INodeParser>();
            switch (parsers)
            {
                case ParserList.All:
                    var interfaceType = typeof(INodeParser);
                    var types = AppDomain.CurrentDomain.GetAssemblies().SelectMany(s => s.GetTypes()).Where(p => interfaceType.IsAssignableFrom(p) && p.IsClass && p != typeof(DefaultParser));
                    foreach (var type in types)
                    {
                        INodeParser instance = (INodeParser) Activator.CreateInstance(type);
                        _parsers.Add(instance);
                    }

                    break;
                case ParserList.Enhanced:
                    _parsers.Add(new StatsSystemParser());
                    _parsers.Add(new StatPoolSystemParser());
                    goto case ParserList.Simple;
                case ParserList.Simple:
                    _parsers.Add(new ItemDataParser());
                    _parsers.Add(new InventoryParser());
                    _parsers.Add(new ItemDropStorageManagerParser());
                    _parsers.Add(new ItemDropStorageParser());
                    _parsers.Add(new CharacterCustomizationAppearancesParser());
                    _parsers.Add(new FactsDBParser());
                    _parsers.Add(new FactsTableParser());
                    break;
            }
            Guid = Guid.NewGuid();
            FlatNodes = new List<NodeEntry>();
            Nodes = new List<NodeEntry>();
            MappingHelper.Init();
        }

        /// <summary>
        /// Creates a new Instance of Save File which will utilize given parsers
        /// </summary>
        /// <param name="parsers">The parsers that will be used for parsing</param>
        public SaveFile(IEnumerable<INodeParser> parsers)
        {
            _parsers = new List<INodeParser>();
            _parsers.AddRange(parsers);
            Guid = Guid.NewGuid();
            FlatNodes = new List<NodeEntry>();
            Nodes = new List<NodeEntry>();
            MappingHelper.Init();
        }

        public void Load(Stream inputStream)
        {
            FlatNodes.Clear();
            Nodes.Clear();

            Stream dataStream;
            using (var reader = new BinaryReader(inputStream, Encoding.ASCII, true))
            {
                ReadHeader(reader);
                ReadNodeInfos(reader);
                dataStream = CompressionHelper.Decompress(reader);
            }

            LoadFromStream(dataStream);
        }

        private void ReadHeader(BinaryReader reader)
        {
            Header = new SaveFileHeader();
            Header.ReadSaveFileHeader(reader);
        }

        private void ReadNodeInfos(BinaryReader reader)
        {
            var magicStartInt = BitConverter.ToUInt32(Encoding.ASCII.GetBytes(Constants.Magic.NODE_INFORMATION_START), 0);
            var magicEndInt = BitConverter.ToUInt32(Encoding.ASCII.GetBytes(Constants.Magic.END_OF_FILE), 0);

            reader.BaseStream.Seek(-8, SeekOrigin.End);
            var infoStart = reader.ReadInt32();
            if (reader.ReadUInt32() != magicEndInt)
                throw new Exception("invalid file format");

            reader.BaseStream.Seek(infoStart, SeekOrigin.Begin);
            if (reader.ReadUInt32() != magicStartInt)
                throw new Exception("invalid file format");

            var length = reader.ReadPackedInt();
            for (var i = 0; i < length; i++)
            {
                var name = reader.ReadPackedString();
                var entry = new NodeEntry();
                entry.NextId = reader.ReadInt32();
                entry.ChildId = reader.ReadInt32();
                entry.Offset = reader.ReadInt32();
                entry.Size = reader.ReadInt32();
                entry.Name = name;
                FlatNodes.Add(entry);
            }

            reader.BaseStream.Position = 0;
        }

        private void LoadFromStream(Stream stream)
        {
            using (BinaryReader reader = new BinaryReader(stream, Encoding.ASCII))
            {
                foreach (var node in FlatNodes)
                {
                    reader.BaseStream.Position = node.Offset;
                    node.Id = reader.ReadInt32();
                    node.Parsers = _parsers;
                }
                foreach (var node in FlatNodes)
                {
                    if (!node.IsChild)
                    {
                        FindChildren(FlatNodes, node, FlatNodes.Count);
                    }
                    if (node.NextId > -1)
                    {
                        node.SetNextNode(FlatNodes.FirstOrDefault(n => n.Id == node.NextId));
                    }
                }
                Nodes.AddRange(FlatNodes.Where(n => !n.IsChild));
                CalculateTrueSizes(Nodes, (int) stream.Length);
                ParserUtils.ParseChildren(Nodes, reader, _parsers);
            }
        }

        public byte[] Save(bool compress = true, bool force32bit = false)
        {
            byte[] result;

            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII))
                {
                    WriterHeader(writer);

                    var uncompressedData = GetNodeData(out var nodeInfos);

                    if (compress)
                    {
                        CompressionHelper.WriteCompressed(writer, uncompressedData, force32bit);
                    }
                    else
                    {
                        CompressionHelper.WriteUncompressed(writer, uncompressedData);
                    }
                    
                    var lastBlockOffset = (int)writer.BaseStream.Position;
                    var footerWithoutLast8Bytes = BuildFooterWithoutLastEightBytes(nodeInfos);

                    writer.Write(footerWithoutLast8Bytes);
                    writer.Write(lastBlockOffset);
                    writer.Write(Encoding.ASCII.GetBytes(Constants.Magic.END_OF_FILE));
                }

                result = stream.ToArray();
            }

            return result;
        }

        public byte[] GetNodeData(out List<NodeInfo> nodeInfos)
        {
            byte[] uncompressedData;

            using (var ms = new MemoryStream())
            {
                using (var nw = new NodeWriter(ms, _parsers))
                {
                    foreach (var node in Nodes)
                    {
                        nw.Write(node);
                    }

                    nodeInfos = nw.GetFinalizedInfos();
                }
                uncompressedData = ms.ToArray();
            }

            return uncompressedData;
        }

        private void WriterHeader(BinaryWriter writer)
        {
            Header.Write(writer);
            writer.Write(Encoding.ASCII.GetBytes(Constants.Magic.SECOND_FILE_HEADER_MAGIC));
            writer.Write(new byte[Constants.Numbers.DEFAULT_HEADER_SIZE - writer.BaseStream.Position]);
        }

        private byte[] BuildFooterWithoutLastEightBytes(List<NodeInfo> nodeInfos)
        {
            byte[] result;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.ASCII))
                {
                    writer.Write(Encoding.ASCII.GetBytes(Constants.Magic.NODE_INFORMATION_START));
                    writer.WritePackedInt(nodeInfos.Count);
                    foreach (var node in nodeInfos)
                    {
                        writer.WritePackedString(node.Name);
                        writer.Write(node.NextId);
                        writer.Write(node.ChildId);
                        writer.Write(node.Offset);
                        writer.Write(node.Size);
                    }
                }
                result = stream.ToArray();
            }
            return result;
        }

        private void CalculateTrueSizes(IReadOnlyList<NodeEntry> nodes, int maxLength)
        {
            for (var i = 0; i < nodes.Count; ++i)
            {
                var currentNode = nodes[i];
                var nextNode = i + 1 < nodes.Count ? nodes[i + 1] : null;

                if (currentNode.Children.Count > 0)
                {
                    // Check if there is a blob before the first child
                    var nextChild = currentNode.Children.First();
                    var blobSize = nextChild.Offset - currentNode.Offset;
                    currentNode.DataSize = blobSize;
                    CalculateTrueSizes(currentNode.Children, maxLength);
                }
                else
                {
                    currentNode.DataSize = currentNode.Size;
                }

                if (nextNode != null)
                {
                    // There is a node after us. Check if there is a blob in between
                    var blobSize = nextNode.Offset - (currentNode.Offset + currentNode.Size);
                    if (blobSize < 0)
                    {
                        throw new InvalidDataException("Found a datablob with negative size");
                    }
                    currentNode.TrailingSize = blobSize;
                }
                else
                {
                    // There might be a blob that is part of the children due to the parents size, check for that
                    if (currentNode.GetParent() == null)
                    {
                        // This is the last node on the root list. Trailing data should have been cought by the last inner child and assigned here but check again.
                        var lastNodeEnd = currentNode.Offset + currentNode.Size;
                        Debug.Assert(lastNodeEnd <= maxLength);
                        if (lastNodeEnd < maxLength)
                        {
                            // There is a trailing blob
                            currentNode.TrailingSize = maxLength - lastNodeEnd;
                        }

                        continue;
                    }
                    nextNode = currentNode.GetParent().GetNextNode();
                    if (nextNode == null)
                    {
                        // This is the last child on the last node. The next valid offset would be the end of the data
                        // Create a virtual node for this so the code below can grab the offset
                        nextNode = new NodeEntry();
                        nextNode.Offset = maxLength;
                    }
                    var parentMax = currentNode.GetParent().Offset + currentNode.GetParent().Size;
                    var childMax = currentNode.Offset + currentNode.Size;
                    // The parent size should never be smaller than the end of the last child.
                    Debug.Assert(parentMax >= childMax);
                    var blobSize = nextNode.Offset - (currentNode.Offset + currentNode.Size);
                    if (blobSize < 0)
                    {
                        throw new InvalidDataException("Found a datablob with negative size");
                    }
                    if (parentMax > childMax)
                    {
                        // Blob belongs to this child
                        currentNode.TrailingSize = blobSize;
                    }
                    else if (parentMax == childMax)
                    {
                        // Blob belongs to the parent but as trailing.
                        currentNode.GetParent().TrailingSize = blobSize;
                    }
                }
            }
        }

        private void FindChildren(List<NodeEntry> nodes, NodeEntry node, int maxNextId)
        {
            if (node.ChildId > -1)
            {
                var nextId = node.NextId;
                if (nextId == -1)
                {
                    nextId = maxNextId;
                }
                for (int i = node.ChildId; i < nextId; i++)
                {
                    var possibleChild = nodes.FirstOrDefault(n => n.Id == i);
                    if (possibleChild == null)
                    {
                        throw new Exception();
                    }
                    if (possibleChild.ChildId > -1)//SubChild
                    {
                        FindChildren(nodes, possibleChild, nextId);
                        node.AddChild(possibleChild);
                    }
                    else
                    {
                        if (!possibleChild.IsChild)//was already added
                        {
                            node.AddChild(possibleChild);
                        }
                        
                    }
                }
            }
        }

        public class NodeInfo
        {
            public string Name { get; set; }
            public int NextId { get; set; }
            public int ChildId { get; set; }
            public int Offset { get; set; }
            public int Size { get; set; }
        }
    }
}
