// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : UMD.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Dumping with a jail-broken PlayStation Portable thru USB.
//
// --[ Description ] ----------------------------------------------------------
//
//     Handles dumping UMD using a jail-broken PlayStation Portable thru USB.
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
using System.Text;
using Aaru.CommonTypes;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Extents;
using Aaru.CommonTypes.Interfaces;
using Aaru.CommonTypes.Structs;
using Aaru.Console;
using Aaru.Core.Logging;
using Aaru.Decoders.SCSI;
using Aaru.Devices;
using Schemas;
using TrackType = Aaru.CommonTypes.Enums.TrackType;
using Version = Aaru.CommonTypes.Interop.Version;

namespace Aaru.Core.Devices.Dumping
{
    public partial class Dump
    {
        void DumpUmd()
        {
            const uint      BLOCK_SIZE    = 2048;
            const MediaType DSK_TYPE      = MediaType.UMD;
            uint            blocksToRead  = 16;
            double          totalDuration = 0;
            double          currentSpeed  = 0;
            double          maxSpeed      = double.MinValue;
            double          minSpeed      = double.MaxValue;
            DateTime        start;
            DateTime        end;

            bool sense = _dev.Read12(out byte[] readBuffer, out _, 0, false, true, false, false, 0, 512, 0, 1, false,
                                     _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Could not read...");
                StoppingErrorMessage?.Invoke("Could not read...");

                return;
            }

            ushort fatStart      = (ushort)((readBuffer[0x0F] << 8)                            + readBuffer[0x0E]);
            ushort sectorsPerFat = (ushort)((readBuffer[0x17] << 8)                            + readBuffer[0x16]);
            ushort rootStart     = (ushort)((sectorsPerFat                                * 2) + fatStart);
            ushort rootSize      = (ushort)((((readBuffer[0x12] << 8) + readBuffer[0x11]) * 32) / 512);
            ushort umdStart      = (ushort)(rootStart + rootSize);

            UpdateStatus?.Invoke($"Reading root directory in sector {rootStart}...");
            _dumpLog.WriteLine("Reading root directory in sector {0}...", rootStart);

            sense = _dev.Read12(out readBuffer, out _, 0, false, true, false, false, rootStart, 512, 0, 1, false,
                                _dev.Timeout, out _);

            if(sense)
            {
                _dumpLog.WriteLine("Could not read...");
                StoppingErrorMessage?.Invoke("Could not read...");

                return;
            }

            uint   umdSizeInBytes  = BitConverter.ToUInt32(readBuffer, 0x3C);
            ulong  blocks          = umdSizeInBytes / BLOCK_SIZE;
            string mediaPartNumber = Encoding.ASCII.GetString(readBuffer, 0, 11).Trim();

            UpdateStatus?.
                Invoke($"Media has {blocks} blocks of {BLOCK_SIZE} bytes/each. (for a total of {blocks * (ulong)BLOCK_SIZE} bytes)");

            UpdateStatus?.Invoke($"Device reports {blocks} blocks ({blocks * BLOCK_SIZE} bytes).");
            UpdateStatus?.Invoke($"Device can read {blocksToRead} blocks at a time.");
            UpdateStatus?.Invoke($"Device reports {BLOCK_SIZE} bytes per logical block.");
            UpdateStatus?.Invoke($"Device reports {2048} bytes per physical block.");
            UpdateStatus?.Invoke($"SCSI device type: {_dev.ScsiType}.");
            UpdateStatus?.Invoke($"Media identified as {DSK_TYPE}.");
            UpdateStatus?.Invoke($"Media part number is {mediaPartNumber}.");
            _dumpLog.WriteLine("Device reports {0} blocks ({1} bytes).", blocks, blocks * BLOCK_SIZE);
            _dumpLog.WriteLine("Device can read {0} blocks at a time.", blocksToRead);
            _dumpLog.WriteLine("Device reports {0} bytes per logical block.", BLOCK_SIZE);
            _dumpLog.WriteLine("Device reports {0} bytes per physical block.", 2048);
            _dumpLog.WriteLine("SCSI device type: {0}.", _dev.ScsiType);
            _dumpLog.WriteLine("Media identified as {0}.", DSK_TYPE);
            _dumpLog.WriteLine("Media part number is {0}.", mediaPartNumber);

            bool ret;

            var mhddLog = new MhddLog(_outputPrefix + ".mhddlog.bin", _dev, blocks, BLOCK_SIZE, blocksToRead, _private);
            var ibgLog  = new IbgLog(_outputPrefix  + ".ibg", 0x0010);
            ret = _outputPlugin.Create(_outputPath, DSK_TYPE, _formatOptions, blocks, BLOCK_SIZE);

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

            (_outputPlugin as IWritableOpticalImage).SetTracks(new List<Track>
            {
                new Track
                {
                    TrackBytesPerSector    = (int)BLOCK_SIZE, TrackEndSector      = blocks - 1, TrackSequence = 1,
                    TrackRawBytesPerSector = (int)BLOCK_SIZE, TrackSubchannelType = TrackSubchannelType.None,
                    TrackSession           = 1, TrackType                         = TrackType.Data
                }
            });

            DumpHardwareType currentTry = null;
            ExtentsULong     extents    = null;

            ResumeSupport.Process(true, _dev.IsRemovable, blocks, _dev.Manufacturer, _dev.Model, _dev.Serial,
                                  _dev.PlatformId, ref _resume, ref currentTry, ref extents, _dev.FirmwareRevision,
                                  _private);

            if(currentTry == null ||
               extents    == null)
            {
                StoppingErrorMessage?.Invoke("Could not process resume file, not continuing...");

                return;
            }

            if(_resume.NextBlock > 0)
                _dumpLog.WriteLine("Resuming from block {0}.", _resume.NextBlock);

            bool newTrim = false;

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

                sense = _dev.Read12(out readBuffer, out _, 0, false, true, false, false, (uint)(umdStart + (i * 4)),
                                    512, 0, blocksToRead * 4, false, _dev.Timeout, out double cmdDuration);

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
                    _outputPlugin.WriteSectors(new byte[BLOCK_SIZE * _skip], i, _skip);
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

                currentSpeed     = (sectorSpeedStart * BLOCK_SIZE) / (1048576 * elapsed);
                sectorSpeedStart = 0;
                timeSpeedStart   = DateTime.UtcNow;
            }

            end = DateTime.UtcNow;
            EndProgress?.Invoke();
            mhddLog.Close();

            ibgLog.Close(_dev, blocks, BLOCK_SIZE, (end - start).TotalSeconds, currentSpeed * 1024,
                         (BLOCK_SIZE * (double)(blocks + 1)) / 1024                         / (totalDuration / 1000),
                         _devicePath);

            UpdateStatus?.Invoke($"Dump finished in {(end - start).TotalSeconds} seconds.");

            UpdateStatus?.
                Invoke($"Average dump speed {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / (totalDuration / 1000):F3} KiB/sec.");

            UpdateStatus?.
                Invoke($"Average write speed {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / imageWriteDuration:F3} KiB/sec.");

            _dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);

            _dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                               ((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / (totalDuration / 1000));

            _dumpLog.WriteLine("Average write speed {0:F3} KiB/sec.",
                               ((double)BLOCK_SIZE * (double)(blocks + 1)) / 1024 / imageWriteDuration);

            #region Trimming
            if(_resume.BadBlocks.Count > 0 &&
               !_aborted                   &&
               _trim                       &&
               newTrim)
            {
                start = DateTime.UtcNow;
                _dumpLog.WriteLine("Trimming skipped sectors");

                ulong[] tmpArray = _resume.BadBlocks.ToArray();
                InitProgress?.Invoke();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.Invoke($"Trimming sector {badSector}");

                    sense = _dev.Read12(out readBuffer, out _, 0, false, true, false, false,
                                        (uint)(umdStart + (badSector * 4)), 512, 0, 4, false, _dev.Timeout,
                                        out double cmdDuration);

                    if(sense || _dev.Error)
                        continue;

                    _resume.BadBlocks.Remove(badSector);
                    extents.Add(badSector);
                    _outputPlugin.WriteSector(readBuffer, badSector);
                }

                EndProgress?.Invoke();
                end = DateTime.UtcNow;
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

                if(_persistent)
                {
                    Modes.ModePage_01 pg;

                    sense = _dev.ModeSense6(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                            _dev.Timeout, out _);

                    if(!sense)
                    {
                        Modes.DecodedMode? dcMode6 = Modes.DecodeMode6(readBuffer, _dev.ScsiType);

                        if(dcMode6.HasValue)
                            foreach(Modes.ModePage modePage in dcMode6.Value.Pages)
                                if(modePage.Page    == 0x01 &&
                                   modePage.Subpage == 0x00)
                                    currentModePage = modePage;
                    }

                    if(currentModePage == null)
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

                    md6 = Modes.EncodeMode6(md, _dev.ScsiType);

                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return damaged blocks).");
                    sense = _dev.ModeSelect(md6, out byte[] senseBuf, true, false, _dev.Timeout, out _);

                    if(sense)
                    {
                        UpdateStatus?.
                            Invoke("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");

                        AaruConsole.DebugWriteLine("Error: {0}", Sense.PrettifySense(senseBuf));

                        _dumpLog.
                            WriteLine("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");
                    }
                    else
                        runningPersistent = true;
                }

                InitProgress?.Invoke();
                repeatRetry:
                ulong[] tmpArray = _resume.BadBlocks.ToArray();

                foreach(ulong badSector in tmpArray)
                {
                    if(_aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        _dumpLog.WriteLine("Aborted!");

                        break;
                    }

                    PulseProgress?.
                        Invoke($"Retrying sector {badSector}, pass {pass}, {(runningPersistent ? "recovering partial data, " : "")}{(forward ? "forward" : "reverse")}");

                    sense = _dev.Read12(out readBuffer, out _, 0, false, true, false, false,
                                        (uint)(umdStart + (badSector * 4)), 512, 0, 4, false, _dev.Timeout,
                                        out double cmdDuration);

                    totalDuration += cmdDuration;

                    if(!sense &&
                       !_dev.Error)
                    {
                        _resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        _outputPlugin.WriteSector(readBuffer, badSector);

                        UpdateStatus?.Invoke(string.Format("Correctly retried block {0} in pass {1}.", badSector,
                                                           pass));

                        _dumpLog.WriteLine("Correctly retried block {0} in pass {1}.", badSector, pass);
                    }
                    else if(runningPersistent)
                        _outputPlugin.WriteSector(readBuffer, badSector);
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

                    md6 = Modes.EncodeMode6(md, _dev.ScsiType);

                    _dumpLog.WriteLine("Sending MODE SELECT to drive (return device to previous status).");
                    sense = _dev.ModeSelect(md6, out _, true, false, _dev.Timeout, out _);
                }

                EndProgress?.Invoke();
                AaruConsole.WriteLine();
            }
            #endregion Error handling

            _resume.BadBlocks.Sort();

            foreach(ulong bad in _resume.BadBlocks)
                _dumpLog.WriteLine("Sector {0} could not be read.", bad);

            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            var metadata = new CommonTypes.Structs.ImageInfo
            {
                Application = "Aaru", ApplicationVersion = Version.GetVersion(), MediaPartNumber = mediaPartNumber
            };

            if(!_outputPlugin.SetMetadata(metadata))
                ErrorMessage?.Invoke("Error {0} setting metadata, continuing..." + Environment.NewLine +
                                     _outputPlugin.ErrorMessage);

            _outputPlugin.SetDumpHardware(_resume.Tries);

            if(_preSidecar != null)
                _outputPlugin.SetCicmMetadata(_preSidecar);

            _dumpLog.WriteLine("Closing output file.");
            UpdateStatus?.Invoke("Closing output file.");
            DateTime closeStart = DateTime.Now;
            _outputPlugin.Close();
            DateTime closeEnd = DateTime.Now;
            _dumpLog.WriteLine("Closed in {0} seconds.", (closeEnd - closeStart).TotalSeconds);

            if(_aborted)
            {
                UpdateStatus?.Invoke("Aborted!");
                _dumpLog.WriteLine("Aborted!");

                return;
            }

            double totalChkDuration = 0;

            if(_metadata)
                WriteOpticalSidecar(BLOCK_SIZE, blocks, DSK_TYPE, null, null, 1, out totalChkDuration, null);

            UpdateStatus?.Invoke("");

            UpdateStatus?.
                Invoke($"Took a total of {(end - start).TotalSeconds:F3} seconds ({totalDuration / 1000:F3} processing commands, {totalChkDuration / 1000:F3} checksumming, {imageWriteDuration:F3} writing, {(closeEnd - closeStart).TotalSeconds:F3} closing).");

            UpdateStatus?.
                Invoke($"Average speed: {((double)BLOCK_SIZE * (double)(blocks + 1)) / 1048576 / (totalDuration / 1000):F3} MiB/sec.");

            UpdateStatus?.Invoke($"Fastest speed burst: {maxSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"Slowest speed burst: {minSpeed:F3} MiB/sec.");
            UpdateStatus?.Invoke($"{_resume.BadBlocks.Count} sectors could not be read.");
            UpdateStatus?.Invoke("");

            Statistics.AddMedia(DSK_TYPE, true);
        }
    }
}