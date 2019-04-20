﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : BlockMedia.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Contains logic to create sidecar from a block media dump.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2019 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DiscImageChef.CommonTypes;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.CommonTypes.Interfaces;
using DiscImageChef.CommonTypes.Metadata;
using DiscImageChef.Console;
using DiscImageChef.Decoders.ATA;
using DiscImageChef.Decoders.PCMCIA;
using DiscImageChef.DiscImages;
using DiscImageChef.Filters;
using Schemas;
using MediaType = DiscImageChef.CommonTypes.Metadata.MediaType;
using Tuple = DiscImageChef.Decoders.PCMCIA.Tuple;

namespace DiscImageChef.Core
{
    public partial class Sidecar
    {
        /// <summary>
        ///     Creates a metadata sidecar for a block media (e.g. floppy, hard disk, flash card, usb stick)
        /// </summary>
        /// <param name="image">Image</param>
        /// <param name="filterId">Filter uuid</param>
        /// <param name="imagePath">Image path</param>
        /// <param name="fi">Image file information</param>
        /// <param name="plugins">Image plugins</param>
        /// <param name="imgChecksums">List of image checksums</param>
        /// <param name="sidecar">Metadata sidecar</param>
        void BlockMedia(IMediaImage        image, Guid filterId, string imagePath, FileInfo fi,
                        PluginBase         plugins,
                        List<ChecksumType> imgChecksums, ref CICMMetadataType sidecar, Encoding encoding)
        {
            if(aborted) return;

            sidecar.BlockMedia = new[]
            {
                new BlockMediaType
                {
                    Checksums = imgChecksums.ToArray(),
                    Image = new ImageType
                    {
                        format          = image.Format,
                        offset          = 0,
                        offsetSpecified = true,
                        Value           = Path.GetFileName(imagePath)
                    },
                    Size     = fi.Length,
                    Sequence = new SequenceType {MediaTitle = image.Info.MediaTitle}
                }
            };

            if(image.Info.MediaSequence != 0 && image.Info.LastMediaSequence != 0)
            {
                sidecar.BlockMedia[0].Sequence.MediaSequence = image.Info.MediaSequence;
                sidecar.BlockMedia[0].Sequence.TotalMedia    = image.Info.LastMediaSequence;
            }
            else
            {
                sidecar.BlockMedia[0].Sequence.MediaSequence = 1;
                sidecar.BlockMedia[0].Sequence.TotalMedia    = 1;
            }

            UpdateStatus("Hashing media tags...");
            foreach(MediaTagType tagType in image.Info.ReadableMediaTags)
            {
                if(aborted) return;

                switch(tagType)
                {
                    case MediaTagType.ATAPI_IDENTIFY:
                        sidecar.BlockMedia[0].ATA = new ATAType
                        {
                            Identify = new DumpType
                            {
                                Checksums =
                                    Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.ATAPI_IDENTIFY))
                                            .ToArray(),
                                Size = image.ReadDiskTag(MediaTagType.ATAPI_IDENTIFY).Length
                            }
                        };
                        break;
                    case MediaTagType.ATA_IDENTIFY:
                        sidecar.BlockMedia[0].ATA = new ATAType
                        {
                            Identify = new DumpType
                            {
                                Checksums = Checksum
                                           .GetChecksums(image.ReadDiskTag(MediaTagType.ATA_IDENTIFY))
                                           .ToArray(),
                                Size = image.ReadDiskTag(MediaTagType.ATA_IDENTIFY).Length
                            }
                        };
                        break;
                    case MediaTagType.PCMCIA_CIS:
                        byte[] cis = image.ReadDiskTag(MediaTagType.PCMCIA_CIS);
                        sidecar.BlockMedia[0].PCMCIA = new PCMCIAType
                        {
                            CIS = new DumpType {Checksums = Checksum.GetChecksums(cis).ToArray(), Size = cis.Length}
                        };
                        Tuple[] tuples = CIS.GetTuples(cis);
                        if(tuples != null)
                            foreach(Tuple tuple in tuples)
                                switch(tuple.Code)
                                {
                                    case TupleCodes.CISTPL_MANFID:
                                        ManufacturerIdentificationTuple manfid =
                                            CIS.DecodeManufacturerIdentificationTuple(tuple);

                                        if(manfid != null)
                                        {
                                            sidecar.BlockMedia[0].PCMCIA.ManufacturerCode =
                                                manfid.ManufacturerID;
                                            sidecar.BlockMedia[0].PCMCIA.CardCode                  = manfid.CardID;
                                            sidecar.BlockMedia[0].PCMCIA.ManufacturerCodeSpecified = true;
                                            sidecar.BlockMedia[0].PCMCIA.CardCodeSpecified         = true;
                                        }

                                        break;
                                    case TupleCodes.CISTPL_VERS_1:
                                        Level1VersionTuple vers = CIS.DecodeLevel1VersionTuple(tuple);

                                        if(vers != null)
                                        {
                                            sidecar.BlockMedia[0].PCMCIA.Manufacturer = vers.Manufacturer;
                                            sidecar.BlockMedia[0].PCMCIA.ProductName  = vers.Product;
                                            sidecar.BlockMedia[0].PCMCIA.Compliance =
                                                $"{vers.MajorVersion}.{vers.MinorVersion}";
                                            sidecar.BlockMedia[0].PCMCIA.AdditionalInformation =
                                                vers.AdditionalInformation;
                                        }

                                        break;
                                }

                        break;
                    case MediaTagType.SCSI_INQUIRY:
                        sidecar.BlockMedia[0].SCSI = new SCSIType
                        {
                            Inquiry = new DumpType
                            {
                                Checksums = Checksum
                                           .GetChecksums(image.ReadDiskTag(MediaTagType.SCSI_INQUIRY))
                                           .ToArray(),
                                Size = image.ReadDiskTag(MediaTagType.SCSI_INQUIRY).Length
                            }
                        };
                        break;
                    case MediaTagType.SD_CID:
                        if(sidecar.BlockMedia[0].SecureDigital == null)
                            sidecar.BlockMedia[0].SecureDigital = new SecureDigitalType();
                        sidecar.BlockMedia[0].SecureDigital.CID = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_CID)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_CID).Length
                        };
                        break;
                    case MediaTagType.SD_CSD:
                        if(sidecar.BlockMedia[0].SecureDigital == null)
                            sidecar.BlockMedia[0].SecureDigital = new SecureDigitalType();
                        sidecar.BlockMedia[0].SecureDigital.CSD = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_CSD)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_CSD).Length
                        };
                        break;
                    case MediaTagType.SD_SCR:
                        if(sidecar.BlockMedia[0].SecureDigital == null)
                            sidecar.BlockMedia[0].SecureDigital = new SecureDigitalType();
                        sidecar.BlockMedia[0].SecureDigital.SCR = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_SCR)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_SCR).Length
                        };
                        break;
                    case MediaTagType.SD_OCR:
                        if(sidecar.BlockMedia[0].SecureDigital == null)
                            sidecar.BlockMedia[0].SecureDigital = new SecureDigitalType();
                        sidecar.BlockMedia[0].SecureDigital.OCR = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_OCR)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_OCR).Length
                        };
                        break;
                    case MediaTagType.MMC_CID:
                        if(sidecar.BlockMedia[0].MultiMediaCard == null)
                            sidecar.BlockMedia[0].MultiMediaCard = new MultiMediaCardType();
                        sidecar.BlockMedia[0].MultiMediaCard.CID = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_CID)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_CID).Length
                        };
                        break;
                    case MediaTagType.MMC_CSD:
                        if(sidecar.BlockMedia[0].MultiMediaCard == null)
                            sidecar.BlockMedia[0].MultiMediaCard = new MultiMediaCardType();
                        sidecar.BlockMedia[0].MultiMediaCard.CSD = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_CSD)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_CSD).Length
                        };
                        break;
                    case MediaTagType.MMC_OCR:
                        if(sidecar.BlockMedia[0].MultiMediaCard == null)
                            sidecar.BlockMedia[0].MultiMediaCard = new MultiMediaCardType();
                        sidecar.BlockMedia[0].MultiMediaCard.OCR = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SD_OCR)).ToArray(),
                            Size      = image.ReadDiskTag(MediaTagType.SD_OCR).Length
                        };
                        break;
                    case MediaTagType.MMC_ExtendedCSD:
                        if(sidecar.BlockMedia[0].MultiMediaCard == null)
                            sidecar.BlockMedia[0].MultiMediaCard = new MultiMediaCardType();
                        sidecar.BlockMedia[0].MultiMediaCard.ExtendedCSD = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.MMC_ExtendedCSD))
                                                .ToArray(),
                            Size = image.ReadDiskTag(MediaTagType.MMC_ExtendedCSD).Length
                        };
                        break;
                    case MediaTagType.USB_Descriptors:
                        if(sidecar.BlockMedia[0].USB == null) sidecar.BlockMedia[0].USB = new USBType();
                        sidecar.BlockMedia[0].USB.Descriptors = new DumpType
                        {
                            Checksums = Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.USB_Descriptors))
                                                .ToArray(),
                            Size = image.ReadDiskTag(MediaTagType.USB_Descriptors).Length
                        };
                        break;
                    case MediaTagType.SCSI_MODESENSE_6:
                        if(sidecar.BlockMedia[0].SCSI == null) sidecar.BlockMedia[0].SCSI = new SCSIType();
                        sidecar.BlockMedia[0].SCSI.ModeSense = new DumpType
                        {
                            Checksums =
                                Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SCSI_MODESENSE_6)).ToArray(),
                            Size = image.ReadDiskTag(MediaTagType.SCSI_MODESENSE_6).Length
                        };
                        break;
                    case MediaTagType.SCSI_MODESENSE_10:
                        if(sidecar.BlockMedia[0].SCSI == null) sidecar.BlockMedia[0].SCSI = new SCSIType();
                        sidecar.BlockMedia[0].SCSI.ModeSense10 = new DumpType
                        {
                            Checksums =
                                Checksum.GetChecksums(image.ReadDiskTag(MediaTagType.SCSI_MODESENSE_10)).ToArray(),
                            Size = image.ReadDiskTag(MediaTagType.SCSI_MODESENSE_10).Length
                        };
                        break;
                }
            }

            // If there is only one track, and it's the same as the image file (e.g. ".iso" files), don't re-checksum.
            if(image.Id == new Guid("12345678-AAAA-BBBB-CCCC-123456789000") &&
               filterId == new Guid("12345678-AAAA-BBBB-CCCC-123456789000"))
                sidecar.BlockMedia[0].ContentChecksums = sidecar.BlockMedia[0].Checksums;
            else
            {
                UpdateStatus("Hashing sectors...");

                Checksum contentChkWorker = new Checksum();

                // For fast debugging, skip checksum
                //goto skipImageChecksum;

                uint  sectorsToRead = 512;
                ulong sectors       = image.Info.Sectors;
                ulong doneSectors   = 0;

                InitProgress2();
                while(doneSectors < sectors)
                {
                    if(aborted)
                    {
                        EndProgress2();
                        return;
                    }

                    byte[] sector;

                    if(sectors - doneSectors >= sectorsToRead)
                    {
                        sector = image.ReadSectors(doneSectors, sectorsToRead);
                        UpdateProgress2("Hashings sector {0} of {1}", (long)doneSectors, (long)sectors);
                        doneSectors += sectorsToRead;
                    }
                    else
                    {
                        sector = image.ReadSectors(doneSectors, (uint)(sectors - doneSectors));
                        UpdateProgress2("Hashings sector {0} of {1}", (long)doneSectors, (long)sectors);
                        doneSectors += sectors - doneSectors;
                    }

                    contentChkWorker.Update(sector);
                }

                // For fast debugging, skip checksum
                //skipImageChecksum:

                List<ChecksumType> cntChecksums = contentChkWorker.End();

                sidecar.BlockMedia[0].ContentChecksums = cntChecksums.ToArray();

                EndProgress2();
            }

            MediaType.MediaTypeToString(image.Info.MediaType, out string dskType, out string dskSubType);
            sidecar.BlockMedia[0].DiskType    = dskType;
            sidecar.BlockMedia[0].DiskSubType = dskSubType;
            Statistics.AddMedia(image.Info.MediaType, false);

            sidecar.BlockMedia[0].Dimensions = Dimensions.DimensionsFromMediaType(image.Info.MediaType);

            sidecar.BlockMedia[0].LogicalBlocks    = (long)image.Info.Sectors;
            sidecar.BlockMedia[0].LogicalBlockSize = (int)image.Info.SectorSize;
            // TODO: Detect it
            sidecar.BlockMedia[0].PhysicalBlockSize = (int)image.Info.SectorSize;

            UpdateStatus("Checking filesystems...");

            if(aborted) return;

            List<Partition> partitions = Partitions.GetAll(image);
            Partitions.AddSchemesToStats(partitions);

            sidecar.BlockMedia[0].FileSystemInformation = new PartitionType[1];
            if(partitions.Count > 0)
            {
                sidecar.BlockMedia[0].FileSystemInformation = new PartitionType[partitions.Count];
                for(int i = 0; i < partitions.Count; i++)
                {
                    if(aborted) return;

                    sidecar.BlockMedia[0].FileSystemInformation[i] = new PartitionType
                    {
                        Description = partitions[i].Description,
                        EndSector   = (int)partitions[i].End,
                        Name        = partitions[i].Name,
                        Sequence    = (int)partitions[i].Sequence,
                        StartSector = (int)partitions[i].Start,
                        Type        = partitions[i].Type
                    };
                    List<FileSystemType> lstFs = new List<FileSystemType>();

                    foreach(IFilesystem plugin in plugins.PluginsList.Values)
                        try
                        {
                            if(aborted) return;

                            if(!plugin.Identify(image, partitions[i])) continue;

                            plugin.GetInformation(image, partitions[i], out _, encoding);
                            lstFs.Add(plugin.XmlFsType);
                            Statistics.AddFilesystem(plugin.XmlFsType.Type);
                        }
                        #pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                        catch
                            #pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                        {
                            //DicConsole.DebugWriteLine("Create-sidecar command", "Plugin {0} crashed", _plugin.Name);
                        }

                    if(lstFs.Count > 0) sidecar.BlockMedia[0].FileSystemInformation[i].FileSystems = lstFs.ToArray();
                }
            }
            else
            {
                if(aborted) return;

                sidecar.BlockMedia[0].FileSystemInformation[0] =
                    new PartitionType {StartSector = 0, EndSector = (int)(image.Info.Sectors - 1)};

                Partition wholePart = new Partition
                {
                    Name   = "Whole device",
                    Length = image.Info.Sectors,
                    Size   = image.Info.Sectors * image.Info.SectorSize
                };

                List<FileSystemType> lstFs = new List<FileSystemType>();

                foreach(IFilesystem plugin in plugins.PluginsList.Values)
                    try
                    {
                        if(aborted) return;

                        if(!plugin.Identify(image, wholePart)) continue;

                        plugin.GetInformation(image, wholePart, out _, encoding);
                        lstFs.Add(plugin.XmlFsType);
                        Statistics.AddFilesystem(plugin.XmlFsType.Type);
                    }
                    #pragma warning disable RECS0022 // A catch clause that catches System.Exception and has an empty body
                    catch
                        #pragma warning restore RECS0022 // A catch clause that catches System.Exception and has an empty body
                    {
                        //DicConsole.DebugWriteLine("Create-sidecar command", "Plugin {0} crashed", _plugin.Name);
                    }

                if(lstFs.Count > 0) sidecar.BlockMedia[0].FileSystemInformation[0].FileSystems = lstFs.ToArray();
            }

            UpdateStatus("Saving metadata...");

            if(image.Info.Cylinders > 0 && image.Info.Heads > 0 && image.Info.SectorsPerTrack > 0)
            {
                sidecar.BlockMedia[0].CylindersSpecified       = true;
                sidecar.BlockMedia[0].HeadsSpecified           = true;
                sidecar.BlockMedia[0].SectorsPerTrackSpecified = true;
                sidecar.BlockMedia[0].Cylinders                = image.Info.Cylinders;
                sidecar.BlockMedia[0].Heads                    = image.Info.Heads;
                sidecar.BlockMedia[0].SectorsPerTrack          = image.Info.SectorsPerTrack;
            }

            if(image.Info.ReadableMediaTags.Contains(MediaTagType.ATA_IDENTIFY))
            {
                Identify.IdentifyDevice? ataId = Identify.Decode(image.ReadDiskTag(MediaTagType.ATA_IDENTIFY));
                if(ataId.HasValue)
                    if(ataId.Value.CurrentCylinders       > 0 && ataId.Value.CurrentHeads > 0 &&
                       ataId.Value.CurrentSectorsPerTrack > 0)
                    {
                        sidecar.BlockMedia[0].CylindersSpecified       = true;
                        sidecar.BlockMedia[0].HeadsSpecified           = true;
                        sidecar.BlockMedia[0].SectorsPerTrackSpecified = true;
                        sidecar.BlockMedia[0].Cylinders                = ataId.Value.CurrentCylinders;
                        sidecar.BlockMedia[0].Heads                    = ataId.Value.CurrentHeads;
                        sidecar.BlockMedia[0].SectorsPerTrack          = ataId.Value.CurrentSectorsPerTrack;
                    }
                    else if(ataId.Value.Cylinders > 0 && ataId.Value.Heads > 0 && ataId.Value.SectorsPerTrack > 0)
                    {
                        sidecar.BlockMedia[0].CylindersSpecified       = true;
                        sidecar.BlockMedia[0].HeadsSpecified           = true;
                        sidecar.BlockMedia[0].SectorsPerTrackSpecified = true;
                        sidecar.BlockMedia[0].Cylinders                = ataId.Value.Cylinders;
                        sidecar.BlockMedia[0].Heads                    = ataId.Value.Heads;
                        sidecar.BlockMedia[0].SectorsPerTrack          = ataId.Value.SectorsPerTrack;
                    }
            }

            if(image.DumpHardware != null) sidecar.BlockMedia[0].DumpHardwareArray = image.DumpHardware.ToArray();

            // TODO: This is more of a hack, redo it planned for >4.0
            string trkFormat = null;

            switch(image.Info.MediaType)
            {
                case CommonTypes.MediaType.Apple32SS:
                case CommonTypes.MediaType.Apple32DS:
                    trkFormat = "Apple GCR (DOS 3.2)";
                    break;
                case CommonTypes.MediaType.Apple33SS:
                case CommonTypes.MediaType.Apple33DS:
                    trkFormat = "Apple GCR (DOS 3.3)";
                    break;
                case CommonTypes.MediaType.AppleSonySS:
                case CommonTypes.MediaType.AppleSonyDS:
                    trkFormat = "Apple GCR (Sony)";
                    break;
                case CommonTypes.MediaType.AppleFileWare:
                    trkFormat = "Apple GCR (Twiggy)";
                    break;
                case CommonTypes.MediaType.DOS_525_SS_DD_9:
                case CommonTypes.MediaType.DOS_525_DS_DD_8:
                case CommonTypes.MediaType.DOS_525_DS_DD_9:
                case CommonTypes.MediaType.DOS_525_HD:
                case CommonTypes.MediaType.DOS_35_SS_DD_8:
                case CommonTypes.MediaType.DOS_35_SS_DD_9:
                case CommonTypes.MediaType.DOS_35_DS_DD_8:
                case CommonTypes.MediaType.DOS_35_DS_DD_9:
                case CommonTypes.MediaType.DOS_35_HD:
                case CommonTypes.MediaType.DOS_35_ED:
                case CommonTypes.MediaType.DMF:
                case CommonTypes.MediaType.DMF_82:
                case CommonTypes.MediaType.XDF_525:
                case CommonTypes.MediaType.XDF_35:
                case CommonTypes.MediaType.IBM53FD_256:
                case CommonTypes.MediaType.IBM53FD_512:
                case CommonTypes.MediaType.IBM53FD_1024:
                case CommonTypes.MediaType.RX02:
                case CommonTypes.MediaType.RX03:
                case CommonTypes.MediaType.RX50:
                case CommonTypes.MediaType.ACORN_525_SS_DD_40:
                case CommonTypes.MediaType.ACORN_525_SS_DD_80:
                case CommonTypes.MediaType.ACORN_525_DS_DD:
                case CommonTypes.MediaType.ACORN_35_DS_DD:
                case CommonTypes.MediaType.ACORN_35_DS_HD:
                case CommonTypes.MediaType.ATARI_525_ED:
                case CommonTypes.MediaType.ATARI_525_DD:
                case CommonTypes.MediaType.ATARI_35_SS_DD:
                case CommonTypes.MediaType.ATARI_35_DS_DD:
                case CommonTypes.MediaType.ATARI_35_SS_DD_11:
                case CommonTypes.MediaType.ATARI_35_DS_DD_11:
                case CommonTypes.MediaType.DOS_525_SS_DD_8:
                case CommonTypes.MediaType.NEC_8_DD:
                case CommonTypes.MediaType.NEC_525_SS:
                case CommonTypes.MediaType.NEC_525_DS:
                case CommonTypes.MediaType.NEC_525_HD:
                case CommonTypes.MediaType.NEC_35_HD_8:
                case CommonTypes.MediaType.NEC_35_HD_15:
                case CommonTypes.MediaType.NEC_35_TD:
                case CommonTypes.MediaType.FDFORMAT_525_DD:
                case CommonTypes.MediaType.FDFORMAT_525_HD:
                case CommonTypes.MediaType.FDFORMAT_35_DD:
                case CommonTypes.MediaType.FDFORMAT_35_HD:
                case CommonTypes.MediaType.Apricot_35:
                case CommonTypes.MediaType.CompactFloppy:
                    trkFormat = "IBM MFM";
                    break;
                case CommonTypes.MediaType.ATARI_525_SD:
                case CommonTypes.MediaType.NEC_8_SD:
                case CommonTypes.MediaType.ACORN_525_SS_SD_40:
                case CommonTypes.MediaType.ACORN_525_SS_SD_80:
                case CommonTypes.MediaType.RX01:
                case CommonTypes.MediaType.IBM23FD:
                case CommonTypes.MediaType.IBM33FD_128:
                case CommonTypes.MediaType.IBM33FD_256:
                case CommonTypes.MediaType.IBM33FD_512:
                case CommonTypes.MediaType.IBM43FD_128:
                case CommonTypes.MediaType.IBM43FD_256:
                    trkFormat = "IBM FM";
                    break;
                case CommonTypes.MediaType.CBM_35_DD:
                    trkFormat = "Commodore MFM";
                    break;
                case CommonTypes.MediaType.CBM_AMIGA_35_HD:
                case CommonTypes.MediaType.CBM_AMIGA_35_DD:
                    trkFormat = "Amiga MFM";
                    break;
                case CommonTypes.MediaType.CBM_1540:
                case CommonTypes.MediaType.CBM_1540_Ext:
                case CommonTypes.MediaType.CBM_1571:
                    trkFormat = "Commodore GCR";
                    break;
                case CommonTypes.MediaType.SHARP_525_9:
                case CommonTypes.MediaType.SHARP_35_9: break;
                case CommonTypes.MediaType.ECMA_99_15:
                case CommonTypes.MediaType.ECMA_99_26:
                case CommonTypes.MediaType.ECMA_99_8:
                    trkFormat = "ISO MFM";
                    break;
                case CommonTypes.MediaType.ECMA_54:
                case CommonTypes.MediaType.ECMA_59:
                case CommonTypes.MediaType.ECMA_66:
                case CommonTypes.MediaType.ECMA_69_8:
                case CommonTypes.MediaType.ECMA_69_15:
                case CommonTypes.MediaType.ECMA_69_26:
                case CommonTypes.MediaType.ECMA_70:
                case CommonTypes.MediaType.ECMA_78:
                case CommonTypes.MediaType.ECMA_78_2:
                    trkFormat = "ISO FM";
                    break;
                default:
                    trkFormat = "Unknown";
                    break;
            }

            #region SuperCardPro
            string scpFilePath = Path.Combine(Path.GetDirectoryName(imagePath),
                                              Path.GetFileNameWithoutExtension(imagePath) + ".scp");

            if(aborted) return;

            if(File.Exists(scpFilePath))
            {
                UpdateStatus("Hashing SuperCardPro image...");
                SuperCardPro scpImage  = new SuperCardPro();
                ZZZNoFilter  scpFilter = new ZZZNoFilter();
                scpFilter.Open(scpFilePath);

                if(image.Info.Heads <= 2 && scpImage.Identify(scpFilter))
                {
                    try { scpImage.Open(scpFilter); }
                    catch(NotImplementedException) { }

                    if(image.Info.Heads == 2 && scpImage.Header.heads == 0 || image.Info.Heads == 1 &&
                       (scpImage.Header.heads == 1 || scpImage.Header.heads == 2))
                        if(scpImage.Header.end + 1 >= image.Info.Cylinders)
                        {
                            List<BlockTrackType> scpBlockTrackTypes = new List<BlockTrackType>();
                            long                 currentSector      = 0;
                            Stream               scpStream          = scpFilter.GetDataForkStream();

                            for(byte t = scpImage.Header.start; t <= scpImage.Header.end; t++)
                            {
                                if(aborted) return;

                                BlockTrackType scpBlockTrackType = new BlockTrackType
                                {
                                    Cylinder = t / image.Info.Heads,
                                    Head     = t % image.Info.Heads,
                                    Image = new ImageType
                                    {
                                        format = scpImage.Format,
                                        Value  = Path.GetFileName(scpFilePath),
                                        offset = scpImage.Header.offsets[t]
                                    }
                                };

                                if(scpBlockTrackType.Cylinder < image.Info.Cylinders)
                                {
                                    scpBlockTrackType.StartSector    =  currentSector;
                                    currentSector                    += image.Info.SectorsPerTrack;
                                    scpBlockTrackType.EndSector      =  currentSector - 1;
                                    scpBlockTrackType.Sectors        =  image.Info.SectorsPerTrack;
                                    scpBlockTrackType.BytesPerSector =  (int)image.Info.SectorSize;
                                    scpBlockTrackType.Format         =  trkFormat;
                                }

                                if(scpImage.ScpTracks.TryGetValue(t, out SuperCardPro.TrackHeader scpTrack))
                                {
                                    byte[] trackContents =
                                        new byte[scpTrack.Entries.Last().dataOffset +
                                                 scpTrack.Entries.Last().trackLength - scpImage.Header.offsets[t] + 1];
                                    scpStream.Position = scpImage.Header.offsets[t];
                                    scpStream.Read(trackContents, 0, trackContents.Length);
                                    scpBlockTrackType.Size      = trackContents.Length;
                                    scpBlockTrackType.Checksums = Checksum.GetChecksums(trackContents).ToArray();
                                }

                                scpBlockTrackTypes.Add(scpBlockTrackType);
                            }

                            sidecar.BlockMedia[0].Track =
                                scpBlockTrackTypes.OrderBy(t => t.Cylinder).ThenBy(t => t.Head).ToArray();
                        }
                        else
                            DicConsole
                               .ErrorWriteLine("SuperCardPro image do not contain same number of tracks ({0}) than disk image ({1}), ignoring...",
                                               scpImage.Header.end + 1, image.Info.Cylinders);
                    else
                        DicConsole
                           .ErrorWriteLine("SuperCardPro image do not contain same number of heads ({0}) than disk image ({1}), ignoring...",
                                           2, image.Info.Heads);
                }
            }
            #endregion

            #region KryoFlux
            string kfFile = null;
            string basename = Path.Combine(Path.GetDirectoryName(imagePath),
                                           Path.GetFileNameWithoutExtension(imagePath));
            bool kfDir = false;
            if(aborted) return;

            if(Directory.Exists(basename))
            {
                string[] possibleKfStarts = Directory.GetFiles(basename, "*.raw", SearchOption.TopDirectoryOnly);
                if(possibleKfStarts.Length > 0)
                {
                    kfFile = possibleKfStarts[0];
                    kfDir  = true;
                }
            }
            else if(File.Exists(basename + "00.0.raw")) kfFile = basename + "00.0.raw";
            else if(File.Exists(basename + "00.1.raw")) kfFile = basename + "00.1.raw";

            if(kfFile != null)
            {
                UpdateStatus("Hashing KryoFlux images...");

                KryoFlux    kfImage  = new KryoFlux();
                ZZZNoFilter kfFilter = new ZZZNoFilter();
                kfFilter.Open(kfFile);
                if(image.Info.Heads <= 2 && kfImage.Identify(kfFilter))
                {
                    try { kfImage.Open(kfFilter); }
                    catch(NotImplementedException) { }

                    if(kfImage.Info.Heads == image.Info.Heads)
                        if(kfImage.Info.Cylinders >= image.Info.Cylinders)
                        {
                            List<BlockTrackType> kfBlockTrackTypes = new List<BlockTrackType>();

                            long currentSector = 0;

                            foreach(KeyValuePair<byte, IFilter> kvp in kfImage.tracks)
                            {
                                if(aborted) return;

                                BlockTrackType kfBlockTrackType = new BlockTrackType
                                {
                                    Cylinder = kvp.Key / image.Info.Heads,
                                    Head     = kvp.Key % image.Info.Heads,
                                    Image = new ImageType
                                    {
                                        format = kfImage.Format,
                                        Value = kfDir
                                                    ? Path
                                                       .Combine(Path.GetFileName(Path.GetDirectoryName(kvp.Value.GetBasePath())),
                                                                kvp.Value.GetFilename())
                                                    : kvp.Value.GetFilename(),
                                        offset = 0
                                    }
                                };

                                if(kfBlockTrackType.Cylinder < image.Info.Cylinders)
                                {
                                    kfBlockTrackType.StartSector    =  currentSector;
                                    currentSector                   += image.Info.SectorsPerTrack;
                                    kfBlockTrackType.EndSector      =  currentSector - 1;
                                    kfBlockTrackType.Sectors        =  image.Info.SectorsPerTrack;
                                    kfBlockTrackType.BytesPerSector =  (int)image.Info.SectorSize;
                                    kfBlockTrackType.Format         =  trkFormat;
                                }

                                Stream kfStream      = kvp.Value.GetDataForkStream();
                                byte[] trackContents = new byte[kfStream.Length];
                                kfStream.Position = 0;
                                kfStream.Read(trackContents, 0, trackContents.Length);
                                kfBlockTrackType.Size      = trackContents.Length;
                                kfBlockTrackType.Checksums = Checksum.GetChecksums(trackContents).ToArray();

                                kfBlockTrackTypes.Add(kfBlockTrackType);
                            }

                            sidecar.BlockMedia[0].Track =
                                kfBlockTrackTypes.OrderBy(t => t.Cylinder).ThenBy(t => t.Head).ToArray();
                        }
                        else
                            DicConsole
                               .ErrorWriteLine("KryoFlux image do not contain same number of tracks ({0}) than disk image ({1}), ignoring...",
                                               kfImage.Info.Cylinders, image.Info.Cylinders);
                    else
                        DicConsole
                           .ErrorWriteLine("KryoFluximage do not contain same number of heads ({0}) than disk image ({1}), ignoring...",
                                           kfImage.Info.Heads, image.Info.Heads);
                }
            }
            #endregion

            #region DiscFerret
            string dfiFilePath = Path.Combine(Path.GetDirectoryName(imagePath),
                                              Path.GetFileNameWithoutExtension(imagePath) + ".dfi");

            if(aborted) return;
            if(!File.Exists(dfiFilePath)) return;

            DiscFerret  dfiImage  = new DiscFerret();
            ZZZNoFilter dfiFilter = new ZZZNoFilter();
            dfiFilter.Open(dfiFilePath);

            if(!dfiImage.Identify(dfiFilter)) return;

            try { dfiImage.Open(dfiFilter); }
            catch(NotImplementedException) { }

            UpdateStatus("Hashing DiscFerret image...");

            if(image.Info.Heads == dfiImage.Info.Heads)
                if(dfiImage.Info.Cylinders >= image.Info.Cylinders)
                {
                    List<BlockTrackType> dfiBlockTrackTypes = new List<BlockTrackType>();
                    long                 currentSector      = 0;
                    Stream               dfiStream          = dfiFilter.GetDataForkStream();

                    foreach(int t in dfiImage.TrackOffsets.Keys)
                    {
                        if(aborted) return;

                        BlockTrackType dfiBlockTrackType = new BlockTrackType
                        {
                            Cylinder = t / image.Info.Heads,
                            Head     = t % image.Info.Heads,
                            Image    = new ImageType {format = dfiImage.Format, Value = Path.GetFileName(dfiFilePath)}
                        };

                        if(dfiBlockTrackType.Cylinder < image.Info.Cylinders)
                        {
                            dfiBlockTrackType.StartSector    =  currentSector;
                            currentSector                    += image.Info.SectorsPerTrack;
                            dfiBlockTrackType.EndSector      =  currentSector - 1;
                            dfiBlockTrackType.Sectors        =  image.Info.SectorsPerTrack;
                            dfiBlockTrackType.BytesPerSector =  (int)image.Info.SectorSize;
                            dfiBlockTrackType.Format         =  trkFormat;
                        }

                        if(dfiImage.TrackOffsets.TryGetValue(t, out long offset) &&
                           dfiImage.TrackLengths.TryGetValue(t, out long length))
                        {
                            dfiBlockTrackType.Image.offset = offset;
                            byte[] trackContents = new byte[length];
                            dfiStream.Position = offset;
                            dfiStream.Read(trackContents, 0, trackContents.Length);
                            dfiBlockTrackType.Size      = trackContents.Length;
                            dfiBlockTrackType.Checksums = Checksum.GetChecksums(trackContents).ToArray();
                        }

                        dfiBlockTrackTypes.Add(dfiBlockTrackType);
                    }

                    sidecar.BlockMedia[0].Track =
                        dfiBlockTrackTypes.OrderBy(t => t.Cylinder).ThenBy(t => t.Head).ToArray();
                }
                else
                    DicConsole
                       .ErrorWriteLine("DiscFerret image do not contain same number of tracks ({0}) than disk image ({1}), ignoring...",
                                       dfiImage.Info.Cylinders, image.Info.Cylinders);
            else
                DicConsole
                   .ErrorWriteLine("DiscFerret image do not contain same number of heads ({0}) than disk image ({1}), ignoring...",
                                   dfiImage.Info.Heads, image.Info.Heads);
            #endregion

            // TODO: Implement support for getting CHS from SCSI mode pages
        }
    }
}