﻿// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : SBC.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Dumps SCSI Block devices.
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
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using DiscImageChef.CommonTypes;
using DiscImageChef.CommonTypes.Enums;
using DiscImageChef.CommonTypes.Extents;
using DiscImageChef.CommonTypes.Interfaces;
using DiscImageChef.CommonTypes.Metadata;
using DiscImageChef.CommonTypes.Structs;
using DiscImageChef.CommonTypes.Structs.Devices.SCSI;
using DiscImageChef.Console;
using DiscImageChef.Core.Logging;
using DiscImageChef.Decoders.SCSI;
using DiscImageChef.Devices;
using Schemas;
using MediaType = DiscImageChef.CommonTypes.MediaType;
using TrackType = DiscImageChef.CommonTypes.Enums.TrackType;
using Version = DiscImageChef.CommonTypes.Interop.Version;

namespace DiscImageChef.Core.Devices.Dumping
{
    /// <summary>Implements dumping SCSI Block Commands and Reduced Block Commands devices</summary>
    partial class Dump
    {
        /// <summary>Dumps a SCSI Block Commands device or a Reduced Block Commands devices</summary>
        /// <param name="opticalDisc">If device contains an optical disc (e.g. DVD or BD)</param>
        /// <param name="mediaTags">Media tags as retrieved in MMC layer</param>
        /// <param name="dskType">Disc type as detected in SCSI or MMC layer</param>
        internal void Sbc(Dictionary<MediaTagType, byte[]> mediaTags, MediaType dskType, bool opticalDisc)
        {
            bool               sense;
            byte               scsiMediumType     = 0;
            byte               scsiDensityCode    = 0;
            bool               containsFloppyPage = false;
            const ushort       SBC_PROFILE        = 0x0001;
            DateTime           start;
            DateTime           end;
            double             totalDuration = 0;
            double             currentSpeed  = 0;
            double             maxSpeed      = double.MinValue;
            double             minSpeed      = double.MaxValue;
            byte[]             readBuffer;
            Modes.DecodedMode? decMode = null;

            if(opticalDisc)
                switch(dskType)
                {
                    case MediaType.REV35:
                    case MediaType.REV70:
                    case MediaType.REV120:
                        opticalDisc = false;

                        break;
                }

            _dumpLog.WriteLine("Initializing reader.");
            var   scsiReader = new Reader(_dev, _dev.Timeout, null, _dumpRaw);
            ulong blocks     = scsiReader.GetDeviceBlocks();
            uint  blockSize  = scsiReader.LogicalBlockSize;

            if(!opticalDisc)
            {
                mediaTags = new Dictionary<MediaTagType, byte[]>();

                if(_dev.IsUsb &&
                   _dev.UsbDescriptors != null)
                    mediaTags.Add(MediaTagType.USB_Descriptors, null);

                if(_dev.Type == DeviceType.ATAPI)
                    mediaTags.Add(MediaTagType.ATAPI_IDENTIFY, null);

                if(_dev.IsPcmcia &&
                   _dev.Cis != null)
                    mediaTags.Add(MediaTagType.PCMCIA_CIS, null);

                sense = _dev.ScsiInquiry(out byte[] cmdBuf, out _);
                mediaTags.Add(MediaTagType.SCSI_INQUIRY, cmdBuf);

                if(!sense)
                {
                    _dumpLog.WriteLine("Requesting MODE SENSE (10).");
                    UpdateStatus?.Invoke("Requesting MODE SENSE (10).");

                    sense = _dev.ModeSense10(out cmdBuf, out _, false, true, ScsiModeSensePageControl.Current, 0x3F,
                                             0xFF, 5, out _);

                    if(!sense ||
                       _dev.Error)
                        sense = _dev.ModeSense10(out cmdBuf, out _, false, true, ScsiModeSensePageControl.Current, 0x3F,
                                                 0x00, 5, out _);

                    if(!sense &&
                       !_dev.Error)
                        if(Modes.DecodeMode10(cmdBuf, _dev.ScsiType).HasValue)
                        {
                            mediaTags.Add(MediaTagType.SCSI_MODESENSE_10, cmdBuf);
                            decMode = Modes.DecodeMode10(cmdBuf, _dev.ScsiType);
                        }

                    _dumpLog.WriteLine("Requesting MODE SENSE (6).");
                    UpdateStatus?.Invoke("Requesting MODE SENSE (6).");

                    sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current, 0x3F, 0x00, 5,
                                            out _);

                    if(sense || _dev.Error)
                        sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current, 0x3F, 0x00,
                                                5, out _);

                    if(sense || _dev.Error)
                        sense = _dev.ModeSense(out cmdBuf, out _, 5, out _);

                    if(!sense &&
                       !_dev.Error)
                        if(Modes.DecodeMode6(cmdBuf, _dev.ScsiType).HasValue)
                        {
                            mediaTags.Add(MediaTagType.SCSI_MODESENSE_6, cmdBuf);
                            decMode = Modes.DecodeMode6(cmdBuf, _dev.ScsiType);
                        }

                    if(decMode.HasValue)
                    {
                        scsiMediumType = (byte)decMode.Value.Header.MediumType;

                        if(decMode.Value.Header.BlockDescriptors        != null &&
                           decMode.Value.Header.BlockDescriptors.Length >= 1)
                            scsiDensityCode = (byte)decMode.Value.Header.BlockDescriptors[0].Density;

                        containsFloppyPage = decMode.Value.Pages != null &&
                                             decMode.Value.Pages.Aggregate(containsFloppyPage,
                                                                           (current, modePage) =>
                                                                               current | (modePage.Page == 0x05));
                    }
                }
            }

            if(dskType == MediaType.Unknown)
                dskType = MediaTypeFromDevice.GetFromScsi((byte)_dev.ScsiType, _dev.Manufacturer, _dev.Model,
                                                          scsiMediumType, scsiDensityCode, blocks + 1, blockSize);

            switch(dskType)
            {
                // Hi-MD devices show the disks while in Hi-MD mode, but they cannot be read using any known command
                // SonicStage changes the device mode, so it is no longer a mass storage device, and can only read
                // tracks written by that same application ID (changes between computers).
                case MediaType.MD:
                    _dumpLog.WriteLine("MiniDisc albums, NetMD discs or user-written audio MiniDisc cannot be dumped.");

                    StoppingErrorMessage?.
                        Invoke("MiniDisc albums, NetMD discs or user-written audio MiniDisc cannot be dumped.");

                    return;
                case MediaType.Unknown when _dev.IsUsb && containsFloppyPage:
                    dskType = MediaType.FlashDrive;

                    break;
            }

            if(scsiReader.FindReadCommand())
            {
                _dumpLog.WriteLine("ERROR: Cannot find correct read command: {0}.", scsiReader.ErrorMessage);
                StoppingErrorMessage?.Invoke("Unable to read medium.");

                return;
            }

            if(blocks    != 0 &&
               blockSize != 0)
            {
                blocks++;

                UpdateStatus?.
                    Invoke($"Media has {blocks} blocks of {blockSize} bytes/each. (for a total of {blocks * (ulong)blockSize} bytes)");
            }

            // Check how many blocks to read, if error show and return
            if(scsiReader.GetBlocksToRead(_maximumReadable))
            {
                _dumpLog.WriteLine("ERROR: Cannot get blocks to read: {0}.", scsiReader.ErrorMessage);
                StoppingErrorMessage?.Invoke(scsiReader.ErrorMessage);

                return;
            }

            uint blocksToRead      = scsiReader.BlocksToRead;
            uint logicalBlockSize  = blockSize;
            uint physicalBlockSize = scsiReader.PhysicalBlockSize;

            if(blocks == 0)
            {
                _dumpLog.WriteLine("ERROR: Unable to read medium or empty medium present...");
                StoppingErrorMessage?.Invoke("Unable to read medium or empty medium present...");

                return;
            }

            UpdateStatus?.Invoke($"Device reports {blocks} blocks ({blocks * blockSize} bytes).");
            UpdateStatus?.Invoke($"Device can read {blocksToRead} blocks at a time.");
            UpdateStatus?.Invoke($"Device reports {blockSize} bytes per logical block.");
            UpdateStatus?.Invoke($"Device reports {scsiReader.LongBlockSize} bytes per physical block.");
            UpdateStatus?.Invoke($"SCSI device type: {_dev.ScsiType}.");
            UpdateStatus?.Invoke($"SCSI medium type: {scsiMediumType}.");
            UpdateStatus?.Invoke($"SCSI density type: {scsiDensityCode}.");
            UpdateStatus?.Invoke($"SCSI floppy mode page present: {containsFloppyPage}.");
            UpdateStatus?.Invoke($"Media identified as {dskType}");

            _dumpLog.WriteLine("Device reports {0} blocks ({1} bytes).", blocks, blocks * blockSize);
            _dumpLog.WriteLine("Device can read {0} blocks at a time.", blocksToRead);
            _dumpLog.WriteLine("Device reports {0} bytes per logical block.", blockSize);
            _dumpLog.WriteLine("Device reports {0} bytes per physical block.", scsiReader.LongBlockSize);
            _dumpLog.WriteLine("SCSI device type: {0}.", _dev.ScsiType);
            _dumpLog.WriteLine("SCSI medium type: {0}.", scsiMediumType);
            _dumpLog.WriteLine("SCSI density type: {0}.", scsiDensityCode);
            _dumpLog.WriteLine("SCSI floppy mode page present: {0}.", containsFloppyPage);
            _dumpLog.WriteLine("Media identified as {0}.", dskType);

            uint longBlockSize = scsiReader.LongBlockSize;

            if(_dumpRaw)
                if(blockSize == longBlockSize)
                {
                    ErrorMessage?.Invoke(!scsiReader.CanReadRaw
                                             ? "Device doesn't seem capable of reading raw data from media."
                                             : "Device is capable of reading raw data but I've been unable to guess correct sector size.");

                    if(!_force)
                    {
                        StoppingErrorMessage?.
                            Invoke("Not continuing. If you want to continue reading cooked data when raw is not available use the force option.");

                        // TODO: Exit more gracefully
                        return;
                    }

                    ErrorMessage?.Invoke("Continuing dumping cooked data.");
                }
                else
                {
                    // Only a block will be read, but it contains 16 sectors and command expect sector number not block number
                    blocksToRead = (uint)(longBlockSize == 37856 ? 16 : 1);

                    UpdateStatus?.
                        Invoke($"Reading {longBlockSize} raw bytes ({blockSize * blocksToRead} cooked bytes) per sector.");

                    physicalBlockSize = longBlockSize;
                    blockSize         = longBlockSize;
                }

            bool ret = true;

            foreach(MediaTagType tag in mediaTags.Keys)
            {
                if(_outputPlugin.SupportedMediaTags.Contains(tag))
                    continue;

                ret = false;
                _dumpLog.WriteLine($"Output format does not support {tag}.");
                ErrorMessage?.Invoke($"Output format does not support {tag}.");
            }

            if(!ret)
            {
                if(_force)
                {
                    _dumpLog.WriteLine("Several media tags not supported, continuing...");
                    ErrorMessage?.Invoke("Several media tags not supported, continuing...");
                }
                else
                {
                    _dumpLog.WriteLine("Several media tags not supported, not continuing...");
                    StoppingErrorMessage?.Invoke("Several media tags not supported, not continuing...");

                    return;
                }
            }

            UpdateStatus?.Invoke($"Reading {blocksToRead} sectors at a time.");
            _dumpLog.WriteLine("Reading {0} sectors at a time.", blocksToRead);

            var mhddLog = new MhddLog(_outputPrefix + ".mhddlog.bin", _dev, blocks, blockSize, blocksToRead);
            var ibgLog  = new IbgLog(_outputPrefix  + ".ibg", SBC_PROFILE);
            ret = _outputPlugin.Create(_outputPath, dskType, _formatOptions, blocks, blockSize);

            // Cannot create image
            if(!ret)
            {
                _dumpLog.WriteLine("Error creating output image, not continuing.");
                _dumpLog.WriteLine(_outputPlugin.ErrorMessage);

                StoppingErrorMessage?.Invoke("Error creating output image, not continuing." + Environment.NewLine +
                                             _outputPlugin.ErrorMessage);

                return;
            }

            start = DateTime.UtcNow;
            double imageWriteDuration = 0;

            if(opticalDisc)
            {
                if(_outputPlugin is IWritableOpticalImage opticalPlugin)
                {
                    opticalPlugin.SetTracks(new List<Track>
                    {
                        new Track
                        {
                            TrackBytesPerSector    = (int)blockSize, TrackEndSector = blocks - 1,
                            TrackSequence          = 1,
                            TrackRawBytesPerSector = (int)blockSize, TrackSubchannelType = TrackSubchannelType.None,
                            TrackSession           = 1, TrackType                        = TrackType.Data
                        }
                    });
                }
                else
                {
                    _dumpLog.WriteLine("The specified plugin does not support storing optical disc images..");
                    StoppingErrorMessage?.Invoke("The specified plugin does not support storing optical disc images.");

                    return;
                }
            }
            else if(decMode?.Pages != null)
            {
                bool setGeometry = false;

                foreach(Modes.ModePage page in decMode.Value.Pages)
                    if(page.Page    == 0x04 &&
                       page.Subpage == 0x00)
                    {
                        Modes.ModePage_04? rigidPage = Modes.DecodeModePage_04(page.PageResponse);

                        if(!rigidPage.HasValue || setGeometry)
                            continue;

                        _dumpLog.WriteLine("Setting geometry to {0} cylinders, {1} heads, {2} sectors per track",
                                           rigidPage.Value.Cylinders, rigidPage.Value.Heads,
                                           (uint)(blocks / (rigidPage.Value.Cylinders * rigidPage.Value.Heads)));

                        UpdateStatus?.
                            Invoke($"Setting geometry to {rigidPage.Value.Cylinders} cylinders, {rigidPage.Value.Heads} heads, {(uint)(blocks / (rigidPage.Value.Cylinders * rigidPage.Value.Heads))} sectors per track");

                        _outputPlugin.SetGeometry(rigidPage.Value.Cylinders, rigidPage.Value.Heads,
                                                  (uint)(blocks / (rigidPage.Value.Cylinders * rigidPage.Value.Heads)));

                        setGeometry = true;
                    }
                    else if(page.Page    == 0x05 &&
                            page.Subpage == 0x00)
                    {
                        Modes.ModePage_05? flexiblePage = Modes.DecodeModePage_05(page.PageResponse);

                        if(!flexiblePage.HasValue)
                            continue;

                        _dumpLog.WriteLine("Setting geometry to {0} cylinders, {1} heads, {2} sectors per track",
                                           flexiblePage.Value.Cylinders, flexiblePage.Value.Heads,
                                           flexiblePage.Value.SectorsPerTrack);

                        UpdateStatus?.
                            Invoke($"Setting geometry to {flexiblePage.Value.Cylinders} cylinders, {flexiblePage.Value.Heads} heads, {flexiblePage.Value.SectorsPerTrack} sectors per track");

                        _outputPlugin.SetGeometry(flexiblePage.Value.Cylinders, flexiblePage.Value.Heads,
                                                  flexiblePage.Value.SectorsPerTrack);

                        setGeometry = true;
                    }
            }

            DumpHardwareType currentTry = null;
            ExtentsULong     extents    = null;

            ResumeSupport.Process(true, _dev.IsRemovable, blocks, _dev.Manufacturer, _dev.Model, _dev.Serial,
                                  _dev.PlatformId, ref _resume, ref currentTry, ref extents, _dev.FirmwareRevision);

            if(currentTry == null ||
               extents    == null)
            {
                StoppingErrorMessage?.Invoke("Could not process resume file, not continuing...");

                return;
            }

            if(_resume.NextBlock > 0)
            {
                UpdateStatus?.Invoke($"Resuming from block {_resume.NextBlock}.");
                _dumpLog.WriteLine("Resuming from block {0}.", _resume.NextBlock);
            }

            // Set speed
            if(_speedMultiplier >= 0)
            {
                _dumpLog.WriteLine($"Setting speed to {_speed}x.");
                UpdateStatus?.Invoke($"Setting speed to {_speed}x.");

                _speed *= _speedMultiplier;

                if(_speed == 0 ||
                   _speed > 0xFFFF)
                    _speed = 0xFFFF;

                _dev.SetCdSpeed(out _, RotationalControl.ClvAndImpureCav, (ushort)_speed, 0, _dev.Timeout, out _);
            }

            bool     newTrim          = false;
            DateTime timeSpeedStart   = DateTime.UtcNow;
            ulong    sectorSpeedStart = 0;
            InitProgress?.Invoke();

            for(ulong i = _resume.NextBlock; i < blocks; i += blocksToRead)
            {
                if(_aborted)
                {
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    UpdateStatus?.Invoke("Aborted!");
                    _dumpLog.WriteLine("Aborted!");

                    break;
                }

                if(blocks - i < blocksToRead)
                    blocksToRead = (uint)(blocks - i);

                #pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                if(currentSpeed > maxSpeed &&
                   currentSpeed != 0)
                    maxSpeed = currentSpeed;

                if(currentSpeed < minSpeed &&
                   currentSpeed != 0)
                    minSpeed = currentSpeed;
                #pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                UpdateProgress?.Invoke($"Reading sector {i} of {blocks} ({currentSpeed:F3} MiB/sec.)", (long)i,
                                       (long)blocks);

                sense         =  scsiReader.ReadBlocks(out readBuffer, i, blocksToRead, out double cmdDuration);
                totalDuration += cmdDuration;

                if(!sense &&
                   !_dev.Error)
                {
                    mhddLog.Write(i, cmdDuration);
                    ibgLog.Write(i, currentSpeed * 1024);
                    DateTime writeStart = DateTime.Now;
                    _outputPlugin.WriteSectors(readBuffer, i, blocksToRead);
                    imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;
                    extents.Add(i, blocksToRead, true);
                }
                else
                {
                    // TODO: Reset device after X errors
                    if(_stopOnError)
                        return; // TODO: Return more cleanly

                    if(i + _skip > blocks)
                        _skip = (uint)(blocks - i);

                    // Write empty data
                    DateTime writeStart = DateTime.Now;
                    _outputPlugin.WriteSectors(new byte[blockSize * _skip], i, _skip);
                    imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;

                    for(ulong b = i; b < i + _skip; b++)
                        _resume.BadBlocks.Add(b);

                    mhddLog.Write(i, cmdDuration < 500 ? 65535 : cmdDuration);

                    ibgLog.Write(i, 0);
                    _dumpLog.WriteLine("Skipping {0} blocks from errored block {1}.", _skip, i);
                    i       += _skip - blocksToRead;
                    newTrim =  true;
                }

                sectorSpeedStart  += blocksToRead;
                _resume.NextBlock =  i + blocksToRead;

                double elapsed = (DateTime.UtcNow - timeSpeedStart).TotalSeconds;

                if(elapsed < 1)
                    continue;

                currentSpeed     = (sectorSpeedStart * blockSize) / (1048576 * elapsed);
                sectorSpeedStart = 0;
                timeSpeedStart   = DateTime.UtcNow;
            }

            end = DateTime.UtcNow;
            EndProgress?.Invoke();
            mhddLog.Close();

            ibgLog.Close(_dev, blocks, blockSize, (end - start).TotalSeconds, currentSpeed * 1024,
                         (blockSize * (double)(blocks + 1)) / 1024                         / (totalDuration / 1000),
                         _devicePath);

            UpdateStatus?.Invoke($"Dump finished in {(end - start).TotalSeconds} seconds.");

            UpdateStatus?.
                Invoke($"Average dump speed {((double)blockSize * (double)(blocks + 1)) / 1024 / (totalDuration / 1000):F3} KiB/sec.");

            UpdateStatus?.
                Invoke($"Average write speed {((double)blockSize * (double)(blocks + 1)) / 1024 / imageWriteDuration:F3} KiB/sec.");

            _dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);

            _dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                               ((double)blockSize * (double)(blocks + 1)) / 1024 / (totalDuration / 1000));

            _dumpLog.WriteLine("Average write speed {0:F3} KiB/sec.",
                               ((double)blockSize * (double)(blocks + 1)) / 1024 / imageWriteDuration);

            #region Trimming
            if(_resume.BadBlocks.Count > 0 &&
               !_aborted                   &&
               _trim                       &&
               newTrim)
            {
                start = DateTime.UtcNow;
                UpdateStatus?.Invoke("Trimming bad sectors");
                _dumpLog.WriteLine("Trimming bad sectors");

                ulong[] tmpArray = _resume.BadBlocks.ToArray();
                InitProgress?.Invoke();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        UpdateStatus?.Invoke("Aborted!");
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.Invoke($"Trimming sector {badSector}");

                    sense = scsiReader.ReadBlock(out readBuffer, badSector, out double cmdDuration);

                    if(sense || _dev.Error)
                        continue;

                    _resume.BadBlocks.Remove(badSector);
                    extents.Add(badSector);
                    _outputPlugin.WriteSector(readBuffer, badSector);
                }

                EndProgress?.Invoke();
                end = DateTime.UtcNow;
                UpdateStatus?.Invoke($"Trimmming finished in {(end - start).TotalSeconds} seconds.");
                _dumpLog.WriteLine("Trimmming finished in {0} seconds.", (end - start).TotalSeconds);
            }
            #endregion Trimming

            #region Error handling
            if(_resume.BadBlocks.Count > 0 &&
               !_aborted                   &&
               _retryPasses > 0)
            {
                int  pass              = 1;
                bool forward           = true;
                bool runningPersistent = false;

                Modes.ModePage? currentModePage = null;
                byte[]          md6;
                byte[]          md10;

                if(_persistent)
                {
                    Modes.ModePage_01_MMC pgMmc;
                    Modes.ModePage_01     pg;

                    sense = _dev.ModeSense6(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                            _dev.Timeout, out _);

                    if(sense)
                    {
                        sense = _dev.ModeSense10(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                                 _dev.Timeout, out _);

                        if(!sense)
                        {
                            Modes.DecodedMode? dcMode10 = Modes.DecodeMode10(readBuffer, _dev.ScsiType);

                            if(dcMode10.HasValue &&
                               dcMode10.Value.Pages != null)
                                foreach(Modes.ModePage modePage in dcMode10.Value.Pages)
                                    if(modePage.Page    == 0x01 &&
                                       modePage.Subpage == 0x00)
                                        currentModePage = modePage;
                        }
                    }
                    else
                    {
                        Modes.DecodedMode? dcMode6 = Modes.DecodeMode6(readBuffer, _dev.ScsiType);

                        if(dcMode6.HasValue &&
                           dcMode6.Value.Pages != null)
                            foreach(Modes.ModePage modePage in dcMode6.Value.Pages)
                                if(modePage.Page    == 0x01 &&
                                   modePage.Subpage == 0x00)
                                    currentModePage = modePage;
                    }

                    if(currentModePage == null)
                    {
                        if(_dev.ScsiType == PeripheralDeviceTypes.MultiMediaDevice)
                        {
                            pgMmc = new Modes.ModePage_01_MMC
                            {
                                PS = false, ReadRetryCount = 32, Parameter = 0x00
                            };

                            currentModePage = new Modes.ModePage
                            {
                                Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                            };
                        }
                        else
                        {
                            pg = new Modes.ModePage_01
                            {
                                PS  = false, AWRE           = true, ARRE = true, TB   = false,
                                RC  = false, EER            = true, PER  = false, DTE = true,
                                DCR = false, ReadRetryCount = 32
                            };

                            currentModePage = new Modes.ModePage
                            {
                                Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01(pg)
                            };
                        }
                    }

                    if(_dev.ScsiType == PeripheralDeviceTypes.MultiMediaDevice)
                    {
                        pgMmc = new Modes.ModePage_01_MMC
                        {
                            PS = false, ReadRetryCount = 255, Parameter = 0x20
                        };

                        var md = new Modes.DecodedMode
                        {
                            Header = new Modes.ModeHeader(), Pages = new[]
                            {
                                new Modes.ModePage
                                {
                                    Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                                }
                            }
                        };

                        md6  = Modes.EncodeMode6(md, _dev.ScsiType);
                        md10 = Modes.EncodeMode10(md, _dev.ScsiType);
                    }
                    else
                    {
                        pg = new Modes.ModePage_01
                        {
                            PS  = false, AWRE           = false, ARRE = false, TB  = true,
                            RC  = false, EER            = true, PER   = false, DTE = false,
                            DCR = false, ReadRetryCount = 255
                        };

                        var md = new Modes.DecodedMode
                        {
                            Header = new Modes.ModeHeader(), Pages = new[]
                            {
                                new Modes.ModePage
                                {
                                    Page = 0x01, Subpage = 0x00, PageResponse = Modes.EncodeModePage_01(pg)
                                }
                            }
                        };

                        md6  = Modes.EncodeMode6(md, _dev.ScsiType);
                        md10 = Modes.EncodeMode10(md, _dev.ScsiType);
                    }

                    UpdateStatus?.Invoke("Sending MODE SELECT to drive (return damaged blocks).");
                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return damaged blocks).");
                    sense = _dev.ModeSelect(md6, out byte[] senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                        sense = _dev.ModeSelect10(md10, out senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                    {
                        UpdateStatus?.
                            Invoke("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");

                        DicConsole.DebugWriteLine("Error: {0}", Sense.PrettifySense(senseBuf));

                        _dumpLog.
                            WriteLine("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");
                    }
                    else
                    {
                        runningPersistent = true;
                    }
                }

                InitProgress?.Invoke();
                repeatRetry:
                ulong[] tmpArray = _resume.BadBlocks.ToArray();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        UpdateStatus?.Invoke("Aborted!");
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.Invoke(string.Format("Retrying sector {0}, pass {1}, {3}{2}", badSector, pass,
                                                        forward ? "forward" : "reverse",
                                                        runningPersistent ? "recovering partial data, " : ""));

                    sense         =  scsiReader.ReadBlock(out readBuffer, badSector, out double cmdDuration);
                    totalDuration += cmdDuration;

                    if(!sense &&
                       !_dev.Error)
                    {
                        _resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        _outputPlugin.WriteSector(readBuffer, badSector);
                        UpdateStatus?.Invoke($"Correctly retried block {badSector} in pass {pass}.");
                        _dumpLog.WriteLine("Correctly retried block {0} in pass {1}.", badSector, pass);
                    }
                    else if(runningPersistent)
                    {
                        _outputPlugin.WriteSector(readBuffer, badSector);
                    }
                }

                if(pass < _retryPasses &&
                   !_aborted           &&
                   _resume.BadBlocks.Count > 0)
                {
                    pass++;
                    forward = !forward;
                    _resume.BadBlocks.Sort();
                    _resume.BadBlocks.Reverse();

                    goto repeatRetry;
                }

                if(runningPersistent && currentModePage.HasValue)
                {
                    var md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(), Pages = new[]
                        {
                            currentModePage.Value
                        }
                    };

                    md6  = Modes.EncodeMode6(md, _dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, _dev.ScsiType);

                    UpdateStatus?.Invoke("Sending MODE SELECT to drive (return device to previous status).");
                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return device to previous status).");
                    sense = _dev.ModeSelect(md6, out _, true, false, _dev.Timeout, out _);

                    if(sense)
                        _dev.ModeSelect10(md10, out _, true, false, _dev.Timeout, out _);
                }

                EndProgress?.Invoke();
            }
            #endregion Error handling

            if(!_aborted)
                if(opticalDisc)
                {
                    foreach(KeyValuePair<MediaTagType, byte[]> tag in mediaTags)
                    {
                        if(tag.Value is null)
                        {
                            DicConsole.ErrorWriteLine("Error: Tag type {0} is null, skipping...", tag.Key);

                            continue;
                        }

                        ret = _outputPlugin.WriteMediaTag(tag.Value, tag.Key);

                        if(ret || _force)
                            continue;

                        // Cannot write tag to image
                        StoppingErrorMessage?.Invoke($"Cannot write tag {tag.Key}.");

                        _dumpLog.WriteLine($"Cannot write tag {tag.Key}." + Environment.NewLine +
                                           _outputPlugin.ErrorMessage);

                        return;
                    }
                }
                else
                {
                    if(!_dev.IsRemovable ||
                       _dev.IsUsb)
                    {
                        if(_dev.IsUsb &&
                           _dev.UsbDescriptors != null)
                        {
                            UpdateStatus?.Invoke("Reading USB descriptors.");
                            _dumpLog.WriteLine("Reading USB descriptors.");
                            ret = _outputPlugin.WriteMediaTag(_dev.UsbDescriptors, MediaTagType.USB_Descriptors);

                            if(!ret &&
                               !_force)
                            {
                                _dumpLog.WriteLine("Cannot write USB descriptors.");

                                StoppingErrorMessage?.Invoke("Cannot write USB descriptors." + Environment.NewLine +
                                                             _outputPlugin.ErrorMessage);

                                return;
                            }
                        }

                        byte[] cmdBuf;

                        if(_dev.Type == DeviceType.ATAPI)
                        {
                            UpdateStatus?.Invoke("Requesting ATAPI IDENTIFY PACKET DEVICE.");
                            _dumpLog.WriteLine("Requesting ATAPI IDENTIFY PACKET DEVICE.");
                            sense = _dev.AtapiIdentify(out cmdBuf, out _);

                            if(!sense)
                            {
                                ret = _outputPlugin.WriteMediaTag(cmdBuf, MediaTagType.ATAPI_IDENTIFY);

                                if(!ret &&
                                   !_force)
                                {
                                    _dumpLog.WriteLine("Cannot write ATAPI IDENTIFY PACKET DEVICE.");

                                    StoppingErrorMessage?.Invoke("Cannot write ATAPI IDENTIFY PACKET DEVICE." +
                                                                 Environment.NewLine                          +
                                                                 _outputPlugin.ErrorMessage);

                                    return;
                                }
                            }
                        }

                        sense = _dev.ScsiInquiry(out cmdBuf, out _);

                        if(!sense)
                        {
                            UpdateStatus?.Invoke("Requesting SCSI INQUIRY.");
                            _dumpLog.WriteLine("Requesting SCSI INQUIRY.");
                            ret = _outputPlugin.WriteMediaTag(cmdBuf, MediaTagType.SCSI_INQUIRY);

                            if(!ret &&
                               !_force)
                            {
                                StoppingErrorMessage?.Invoke("Cannot write SCSI INQUIRY.");

                                _dumpLog.WriteLine("Cannot write SCSI INQUIRY." + Environment.NewLine +
                                                   _outputPlugin.ErrorMessage);

                                return;
                            }

                            UpdateStatus?.Invoke("Requesting MODE SENSE (10).");
                            _dumpLog.WriteLine("Requesting MODE SENSE (10).");

                            sense = _dev.ModeSense10(out cmdBuf, out _, false, true, ScsiModeSensePageControl.Current,
                                                     0x3F, 0xFF, 5, out _);

                            if(!sense ||
                               _dev.Error)
                                sense = _dev.ModeSense10(out cmdBuf, out _, false, true,
                                                         ScsiModeSensePageControl.Current, 0x3F, 0x00, 5, out _);

                            decMode = null;

                            if(!sense &&
                               !_dev.Error)
                                if(Modes.DecodeMode10(cmdBuf, _dev.ScsiType).HasValue)
                                {
                                    decMode = Modes.DecodeMode10(cmdBuf, _dev.ScsiType);
                                    ret     = _outputPlugin.WriteMediaTag(cmdBuf, MediaTagType.SCSI_MODESENSE_10);

                                    if(!ret &&
                                       !_force)
                                    {
                                        _dumpLog.WriteLine("Cannot write SCSI MODE SENSE (10).");

                                        StoppingErrorMessage?.Invoke("Cannot write SCSI MODE SENSE (10)." +
                                                                     Environment.NewLine                  +
                                                                     _outputPlugin.ErrorMessage);

                                        return;
                                    }
                                }

                            UpdateStatus?.Invoke("Requesting MODE SENSE (6).");
                            _dumpLog.WriteLine("Requesting MODE SENSE (6).");

                            sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current, 0x3F,
                                                    0x00, 5, out _);

                            if(sense || _dev.Error)
                                sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current,
                                                        0x3F, 0x00, 5, out _);

                            if(sense || _dev.Error)
                                sense = _dev.ModeSense(out cmdBuf, out _, 5, out _);

                            if(!sense &&
                               !_dev.Error)
                                if(Modes.DecodeMode6(cmdBuf, _dev.ScsiType).HasValue)
                                {
                                    decMode = Modes.DecodeMode6(cmdBuf, _dev.ScsiType);
                                    ret     = _outputPlugin.WriteMediaTag(cmdBuf, MediaTagType.SCSI_MODESENSE_6);

                                    if(!ret &&
                                       !_force)
                                    {
                                        _dumpLog.WriteLine("Cannot write SCSI MODE SENSE (6).");

                                        StoppingErrorMessage?.Invoke("Cannot write SCSI MODE SENSE (6)." +
                                                                     Environment.NewLine                 +
                                                                     _outputPlugin.ErrorMessage);

                                        return;
                                    }
                                }
                        }
                    }
                }

            _resume.BadBlocks.Sort();

            foreach(ulong bad in _resume.BadBlocks)
                _dumpLog.WriteLine("Sector {0} could not be read.", bad);

            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            _outputPlugin.SetDumpHardware(_resume.Tries);

            // TODO: Media Serial Number
            // TODO: Non-removable drive information
            var metadata = new CommonTypes.Structs.ImageInfo
            {
                Application = "DiscImageChef", ApplicationVersion = Version.GetVersion()
            };

            if(!_outputPlugin.SetMetadata(metadata))
                ErrorMessage?.Invoke("Error {0} setting metadata, continuing..." + Environment.NewLine +
                                     _outputPlugin.ErrorMessage);

            if(_preSidecar != null)
                _outputPlugin.SetCicmMetadata(_preSidecar);

            _dumpLog.WriteLine("Closing output file.");
            UpdateStatus?.Invoke("Closing output file.");
            DateTime closeStart = DateTime.Now;
            _outputPlugin.Close();
            DateTime closeEnd = DateTime.Now;
            UpdateStatus?.Invoke($"Closed in {(closeEnd - closeStart).TotalSeconds} seconds.");
            _dumpLog.WriteLine("Closed in {0} seconds.", (closeEnd - closeStart).TotalSeconds);

            if(_aborted)
            {
                UpdateStatus?.Invoke("Aborted!");
                _dumpLog.WriteLine("Aborted!");

                return;
            }

            double totalChkDuration = 0;

            if(_metadata)
            {
                // TODO: Layers
                if(opticalDisc)
                    WriteOpticalSidecar(blockSize, blocks, dskType, null, mediaTags, 1, out totalChkDuration, null);
                else
                {
                    UpdateStatus?.Invoke("Creating sidecar.");
                    _dumpLog.WriteLine("Creating sidecar.");
                    var         filters     = new FiltersList();
                    IFilter     filter      = filters.GetFilter(_outputPath);
                    IMediaImage inputPlugin = ImageFormat.Detect(filter);

                    if(!inputPlugin.Open(filter))
                    {
                        StoppingErrorMessage?.Invoke("Could not open created image.");

                        return;
                    }

                    DateTime chkStart = DateTime.UtcNow;
                    _sidecarClass                      =  new Sidecar(inputPlugin, _outputPath, filter.Id, _encoding);
                    _sidecarClass.InitProgressEvent    += InitProgress;
                    _sidecarClass.UpdateProgressEvent  += UpdateProgress;
                    _sidecarClass.EndProgressEvent     += EndProgress;
                    _sidecarClass.InitProgressEvent2   += InitProgress2;
                    _sidecarClass.UpdateProgressEvent2 += UpdateProgress2;
                    _sidecarClass.EndProgressEvent2    += EndProgress2;
                    _sidecarClass.UpdateStatusEvent    += UpdateStatus;
                    CICMMetadataType sidecar = _sidecarClass.Create();
                    end = DateTime.UtcNow;

                    totalChkDuration = (end - chkStart).TotalMilliseconds;
                    UpdateStatus?.Invoke($"Sidecar created in {(end - chkStart).TotalSeconds} seconds.");

                    UpdateStatus?.
                        Invoke($"Average checksum speed {((double)blockSize * (double)(blocks + 1)) / 1024 / (totalChkDuration / 1000):F3} KiB/sec.");

                    _dumpLog.WriteLine("Sidecar created in {0} seconds.", (end - chkStart).TotalSeconds);

                    _dumpLog.WriteLine("Average checksum speed {0:F3} KiB/sec.",
                                       ((double)blockSize * (double)(blocks + 1)) / 1024 / (totalChkDuration / 1000));

                    if(_preSidecar != null)
                    {
                        _preSidecar.BlockMedia = sidecar.BlockMedia;
                        sidecar                = _preSidecar;
                    }

                    // All USB flash drives report as removable, even if the media is not removable
                    if(!_dev.IsRemovable ||
                       _dev.IsUsb)
                    {
                        if(_dev.IsUsb &&
                           _dev.UsbDescriptors != null)
                            if(_outputPlugin.SupportedMediaTags.Contains(MediaTagType.USB_Descriptors))
                                sidecar.BlockMedia[0].USB = new USBType
                                {
                                    ProductID = _dev.UsbProductId, VendorID = _dev.UsbVendorId, Descriptors =
                                        new DumpType
                                        {
                                            Image     = _outputPath, Size = (ulong)_dev.UsbDescriptors.Length,
                                            Checksums = Checksum.GetChecksums(_dev.UsbDescriptors).ToArray()
                                        }
                                };

                        byte[] cmdBuf;

                        if(_dev.Type == DeviceType.ATAPI)
                        {
                            sense = _dev.AtapiIdentify(out cmdBuf, out _);

                            if(!sense)
                                if(_outputPlugin.SupportedMediaTags.Contains(MediaTagType.ATAPI_IDENTIFY))
                                    sidecar.BlockMedia[0].ATA = new ATAType
                                    {
                                        Identify = new DumpType
                                        {
                                            Image     = _outputPath, Size = (ulong)cmdBuf.Length,
                                            Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                        }
                                    };
                        }

                        sense = _dev.ScsiInquiry(out cmdBuf, out _);

                        if(!sense)
                        {
                            if(_outputPlugin.SupportedMediaTags.Contains(MediaTagType.SCSI_INQUIRY))
                                sidecar.BlockMedia[0].SCSI = new SCSIType
                                {
                                    Inquiry = new DumpType
                                    {
                                        Image     = _outputPath, Size = (ulong)cmdBuf.Length,
                                        Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                    }
                                };

                            // TODO: SCSI Extended Vendor Page descriptors
                            /*
                            UpdateStatus?.Invoke("Reading SCSI Extended Vendor Page Descriptors.");
                            dumpLog.WriteLine("Reading SCSI Extended Vendor Page Descriptors.");
                            sense = dev.ScsiInquiry(out cmdBuf, out _, 0x00);
                            if(!sense)
                            {
                                byte[] pages = EVPD.DecodePage00(cmdBuf);

                                if(pages != null)
                                {
                                    List<EVPDType> evpds = new List<EVPDType>();
                                    foreach(byte page in pages)
                                    {
                                        dumpLog.WriteLine("Requesting page {0:X2}h.", page);
                                        sense = dev.ScsiInquiry(out cmdBuf, out _, page);
                                        if(sense) continue;

                                        EVPDType evpd = new EVPDType
                                        {
                                            Image = $"{outputPrefix}.evpd_{page:X2}h.bin",
                                            Checksums = Checksum.GetChecksums(cmdBuf).ToArray(),
                                            Size = cmdBuf.Length
                                        };
                                        evpd.Checksums = Checksum.GetChecksums(cmdBuf).ToArray();
                                        DataFile.WriteTo("SCSI Dump", evpd.Image, cmdBuf);
                                        evpds.Add(evpd);
                                    }

                                    if(evpds.Count > 0) sidecar.BlockMedia[0].SCSI.EVPD = evpds.ToArray();
                                }
                            }
                            */

                            UpdateStatus?.Invoke("Requesting MODE SENSE (10).");
                            _dumpLog.WriteLine("Requesting MODE SENSE (10).");

                            sense = _dev.ModeSense10(out cmdBuf, out _, false, true, ScsiModeSensePageControl.Current,
                                                     0x3F, 0xFF, 5, out _);

                            if(!sense ||
                               _dev.Error)
                                sense = _dev.ModeSense10(out cmdBuf, out _, false, true,
                                                         ScsiModeSensePageControl.Current, 0x3F, 0x00, 5, out _);

                            decMode = null;

                            if(!sense &&
                               !_dev.Error)
                                if(Modes.DecodeMode10(cmdBuf, _dev.ScsiType).HasValue)
                                    if(_outputPlugin.SupportedMediaTags.Contains(MediaTagType.SCSI_MODESENSE_10))
                                        sidecar.BlockMedia[0].SCSI.ModeSense10 = new DumpType
                                        {
                                            Image     = _outputPath, Size = (ulong)cmdBuf.Length,
                                            Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                        };

                            UpdateStatus?.Invoke("Requesting MODE SENSE (6).");
                            _dumpLog.WriteLine("Requesting MODE SENSE (6).");

                            sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current, 0x3F,
                                                    0x00, 5, out _);

                            if(sense || _dev.Error)
                                sense = _dev.ModeSense6(out cmdBuf, out _, false, ScsiModeSensePageControl.Current,
                                                        0x3F, 0x00, 5, out _);

                            if(sense || _dev.Error)
                                sense = _dev.ModeSense(out cmdBuf, out _, 5, out _);

                            if(!sense &&
                               !_dev.Error)
                                if(Modes.DecodeMode6(cmdBuf, _dev.ScsiType).HasValue)
                                    if(_outputPlugin.SupportedMediaTags.Contains(MediaTagType.SCSI_MODESENSE_6))
                                        sidecar.BlockMedia[0].SCSI.ModeSense = new DumpType
                                        {
                                            Image     = _outputPath, Size = (ulong)cmdBuf.Length,
                                            Checksums = Checksum.GetChecksums(cmdBuf).ToArray()
                                        };
                        }
                    }

                    List<(ulong start, string type)> filesystems = new List<(ulong start, string type)>();

                    if(sidecar.BlockMedia[0].FileSystemInformation != null)
                        filesystems.AddRange(from partition in sidecar.BlockMedia[0].FileSystemInformation
                                             where partition.FileSystems != null
                                             from fileSystem in partition.FileSystems
                                             select (partition.StartSector, fileSystem.Type));

                    if(filesystems.Count > 0)
                        foreach(var filesystem in filesystems.Select(o => new
                        {
                            o.start, o.type
                        }).Distinct())
                        {
                            UpdateStatus?.Invoke($"Found filesystem {filesystem.type} at sector {filesystem.start}");
                            _dumpLog.WriteLine("Found filesystem {0} at sector {1}", filesystem.type, filesystem.start);
                        }

                    sidecar.BlockMedia[0].Dimensions = Dimensions.DimensionsFromMediaType(dskType);
                    (string type, string subType) xmlType = CommonTypes.Metadata.MediaType.MediaTypeToString(dskType);
                    sidecar.BlockMedia[0].DiskType    = xmlType.type;
                    sidecar.BlockMedia[0].DiskSubType = xmlType.subType;

                    // TODO: Implement device firmware revision
                    if(!_dev.IsRemovable ||
                       _dev.IsUsb)
                        if(_dev.Type == DeviceType.ATAPI)
                            sidecar.BlockMedia[0].Interface = "ATAPI";
                        else if(_dev.IsUsb)
                            sidecar.BlockMedia[0].Interface = "USB";
                        else if(_dev.IsFireWire)
                            sidecar.BlockMedia[0].Interface = "FireWire";
                        else
                            sidecar.BlockMedia[0].Interface = "SCSI";

                    sidecar.BlockMedia[0].LogicalBlocks     = blocks;
                    sidecar.BlockMedia[0].PhysicalBlockSize = physicalBlockSize;
                    sidecar.BlockMedia[0].LogicalBlockSize  = logicalBlockSize;
                    sidecar.BlockMedia[0].Manufacturer      = _dev.Manufacturer;
                    sidecar.BlockMedia[0].Model             = _dev.Model;
                    sidecar.BlockMedia[0].Serial            = _dev.Serial;
                    sidecar.BlockMedia[0].Size              = blocks * blockSize;

                    if(_dev.IsRemovable)
                        sidecar.BlockMedia[0].DumpHardwareArray = _resume.Tries.ToArray();

                    UpdateStatus?.Invoke("Writing metadata sidecar");

                    var xmlFs = new FileStream(_outputPrefix + ".cicm.xml", FileMode.Create);

                    var xmlSer = new XmlSerializer(typeof(CICMMetadataType));
                    xmlSer.Serialize(xmlFs, sidecar);
                    xmlFs.Close();
                }
            }

            UpdateStatus?.Invoke("");

            UpdateStatus?.
                Invoke($"Took a total of {(end - start).TotalSeconds:F3} seconds ({totalDuration / 1000:F3} processing commands, {totalChkDuration / 1000:F3} checksumming, {imageWriteDuration:F3} writing, {(closeEnd - closeStart).TotalSeconds:F3} closing).");

            UpdateStatus?.
                Invoke($"Average speed: {((double)blockSize * (double)(blocks + 1)) / 1048576 / (totalDuration / 1000):F3} MiB/sec.");

            UpdateStatus?.Invoke($"Fastest speed burst: {maxSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"Slowest speed burst: {minSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"{_resume.BadBlocks.Count} sectors could not be read.");
            UpdateStatus?.Invoke("");

            Statistics.AddMedia(dskType, true);
        }
    }
}