using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using FileSystemIDandChk;

namespace FileSystemIDandChk.PartPlugins
{
	class AppleMap : PartPlugin
	{
		private const UInt16 APM_MAGIC  = 0x4552; // "ER"
		private const UInt16 APM_ENTRY  = 0x504D; // "PM"
		private const UInt16 APM_OLDENT = 0x5453; // "TS", old entry magic

		public AppleMap (PluginBase Core)
		{
            Name = "Apple Partition Map";
			PluginUUID = new Guid("36405F8D-4F1A-07F5-209C-223D735D6D22");
		}
		
        public override bool GetInformation (ImagePlugins.ImagePlugin imagePlugin, out List<Partition> partitions)
		{
			byte[] cString;
			
			ulong apm_entries;
            uint sector_size;

            if (imagePlugin.GetSectorSize() == 2352 || imagePlugin.GetSectorSize() == 2448)
                sector_size = 2048;
            else
                sector_size = imagePlugin.GetSectorSize();
			
			partitions = new List<Partition>();
			
			AppleMapBootEntry APMB = new AppleMapBootEntry();
			AppleMapPartitionEntry APMEntry = new AppleMapPartitionEntry();

            byte[] APMB_sector = imagePlugin.ReadSector(0);

            APMB.signature = BigEndianBitConverter.ToUInt16(APMB_sector, 0x00);
            APMB.sector_size = BigEndianBitConverter.ToUInt16(APMB_sector, 0x02);
            APMB.sectors = BigEndianBitConverter.ToUInt32(APMB_sector, 0x04);
            APMB.reserved1 = BigEndianBitConverter.ToUInt16(APMB_sector, 0x08);
            APMB.reserved2 = BigEndianBitConverter.ToUInt16(APMB_sector, 0x0A);
            APMB.reserved3 = BigEndianBitConverter.ToUInt32(APMB_sector, 0x0C);
            APMB.driver_entries = BigEndianBitConverter.ToUInt16(APMB_sector, 0x10);
            APMB.first_driver_blk = BigEndianBitConverter.ToUInt32(APMB_sector, 0x12);
            APMB.driver_size = BigEndianBitConverter.ToUInt16(APMB_sector, 0x16);
            APMB.operating_system = BigEndianBitConverter.ToUInt16(APMB_sector, 0x18);

            ulong first_sector = 0;

            if (APMB.signature == APM_MAGIC) // APM boot block found, APM starts in next sector
                first_sector = 1;

            // Read first entry
            byte[] APMEntry_sector = imagePlugin.ReadSector(first_sector);
            APMEntry.signature = BigEndianBitConverter.ToUInt16(APMEntry_sector, 0x00);
            APMEntry.reserved1 = BigEndianBitConverter.ToUInt16(APMEntry_sector, 0x02);
            APMEntry.entries = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x04);
            APMEntry.start = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x08);
            APMEntry.sectors = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x0C);
            cString = new byte[32];
            Array.Copy(APMEntry_sector, 0x10, cString, 0, 32);
            APMEntry.name = StringHandlers.CToString(cString);
            cString = new byte[32];
            Array.Copy(APMEntry_sector, 0x30, cString, 0, 32);
            APMEntry.type = StringHandlers.CToString(cString);
            APMEntry.first_data_block = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x50);
            APMEntry.data_sectors = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x54);
            APMEntry.status = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x58);
            APMEntry.first_boot_block = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x5C);
            APMEntry.boot_size = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x60);
            APMEntry.load_address = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x64);
            APMEntry.reserved2 = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x68);
            APMEntry.entry_point = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x6C);
            APMEntry.reserved3 = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x70);
            APMEntry.checksum = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x74);
            cString = new byte[16];
            Array.Copy(APMEntry_sector, 0x78, cString, 0, 16);
            APMEntry.processor = StringHandlers.CToString(cString);

            if (APMEntry.signature != APM_ENTRY && APMEntry.signature != APM_OLDENT)
                return false;

            if (APMEntry.entries <= 1)
                return false;

            apm_entries = APMEntry.entries;
			
            for(ulong i = 0; i < apm_entries; i++) // For each partition
			{
                APMEntry = new AppleMapPartitionEntry();
                APMEntry_sector = imagePlugin.ReadSector(first_sector + i);
                APMEntry.signature = BigEndianBitConverter.ToUInt16(APMEntry_sector, 0x00);
                APMEntry.reserved1 = BigEndianBitConverter.ToUInt16(APMEntry_sector, 0x02);
                APMEntry.entries = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x04);
                APMEntry.start = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x08);
                APMEntry.sectors = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x0C);
                cString = new byte[32];
                Array.Copy(APMEntry_sector, 0x10, cString, 0, 32);
                APMEntry.name = StringHandlers.CToString(cString);
                cString = new byte[32];
                Array.Copy(APMEntry_sector, 0x30, cString, 0, 32);
                APMEntry.type = StringHandlers.CToString(cString);
                APMEntry.first_data_block = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x50);
                APMEntry.data_sectors = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x54);
                APMEntry.status = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x58);
                APMEntry.first_boot_block = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x5C);
                APMEntry.boot_size = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x60);
                APMEntry.load_address = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x64);
                APMEntry.reserved2 = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x68);
                APMEntry.entry_point = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x6C);
                APMEntry.reserved3 = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x70);
                APMEntry.checksum = BigEndianBitConverter.ToUInt32(APMEntry_sector, 0x74);
                cString = new byte[16];
                Array.Copy(APMEntry_sector, 0x78, cString, 0, 16);
                APMEntry.processor = StringHandlers.CToString(cString);

                if(APMEntry.signature == APM_ENTRY || APMEntry.signature == APM_OLDENT) // It should have partition entry signature
				{
					Partition _partition = new Partition();
					StringBuilder sb = new StringBuilder();
					
					_partition.PartitionSequence = i;
					_partition.PartitionType = APMEntry.type;
					_partition.PartitionName = APMEntry.name;
                    _partition.PartitionStart = APMEntry.start * sector_size;
                    _partition.PartitionLength = APMEntry.sectors * sector_size;
                    _partition.PartitionStartSector = APMEntry.start;
                    _partition.PartitionSectors = APMEntry.sectors;
					
					sb.AppendLine("Partition flags:");
					if((APMEntry.status & 0x01) == 0x01)
						sb.AppendLine("Partition is valid.");
					if((APMEntry.status & 0x02) == 0x02)
						sb.AppendLine("Partition entry is not available.");
					if((APMEntry.status & 0x04) == 0x04)
						sb.AppendLine("Partition is mounted.");
					if((APMEntry.status & 0x08) == 0x08)
						sb.AppendLine("Partition is bootable.");
					if((APMEntry.status & 0x10) == 0x10)
						sb.AppendLine("Partition is readable.");
					if((APMEntry.status & 0x20) == 0x20)
						sb.AppendLine("Partition is writable.");
					if((APMEntry.status & 0x40) == 0x40)
						sb.AppendLine("Partition's boot code is position independent.");
					
					if((APMEntry.status & 0x08) == 0x08)
					{
						sb.AppendFormat("First boot sector: {0}", APMEntry.first_boot_block).AppendLine();
						sb.AppendFormat("Boot is {0} bytes.", APMEntry.boot_size).AppendLine();
						sb.AppendFormat("Boot load address: 0x{0:X8}", APMEntry.load_address).AppendLine();
						sb.AppendFormat("Boot entry point: 0x{0:X8}", APMEntry.entry_point).AppendLine();
						sb.AppendFormat("Boot code checksum: 0x{0:X8}", APMEntry.checksum).AppendLine();
						sb.AppendFormat("Processor: {0}", APMEntry.processor).AppendLine();
					}
					
					_partition.PartitionDescription = sb.ToString();
					
					if((APMEntry.status & 0x01) == 0x01)
						if(APMEntry.type != "Apple_partition_map")
							partitions.Add(_partition);
				}
			}
			
			return true;
		}
		
		public struct AppleMapBootEntry
		{
			public UInt16 signature;        // Signature ("ER")
			public UInt16 sector_size;      // Byter per sector
			public UInt32 sectors;          // Sectors of the disk
			public UInt16 reserved1;        // Reserved
			public UInt16 reserved2;        // Reserved
			public UInt32 reserved3;        // Reserved
			public UInt16 driver_entries;   // Number of entries of the driver descriptor
			public UInt32 first_driver_blk; // First sector of the driver
			public UInt16 driver_size;      // Size in 512bytes sectors of the driver
			public UInt16 operating_system; // Operating system (MacOS = 1)	
		}
		
		public struct AppleMapPartitionEntry
		{
			public UInt16 signature;        // Signature ("PM" or "TS")
			public UInt16 reserved1;        // Reserved
			public UInt32 entries;          // Number of entries on the partition map, each one sector
			public UInt32 start;            // First sector of the partition
			public UInt32 sectors;          // Number of sectos of the partition
			public string name;             // Partition name, 32 bytes, null-padded
			public string type;             // Partition type. 32 bytes, null-padded
			public UInt32 first_data_block; // First sector of the data area
			public UInt32 data_sectors;     // Number of sectors of the data area
			public UInt32 status;           // Partition status
			public UInt32 first_boot_block; // First sector of the boot code
			public UInt32 boot_size;        // Size in bytes of the boot code
			public UInt32 load_address;     // Load address of the boot code
			public UInt32 reserved2;        // Reserved
			public UInt32 entry_point;      // Entry point of the boot code
			public UInt32 reserved3;        // Reserved
			public UInt32 checksum;         // Boot code checksum
			public string processor;        // Processor type, 16 bytes, null-padded
		}
	}
}