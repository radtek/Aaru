// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Trim.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : CompactDisc dumping.
//
// --[ Description ] ----------------------------------------------------------
//
//     Trims skipped sectors when dumping CompactDiscs.
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
using System.Linq;
using Aaru.CommonTypes.Extents;
using Aaru.CommonTypes.Interfaces;
using Aaru.CommonTypes.Structs;
using Aaru.Core.Logging;
using Aaru.Devices;
using Schemas;

// ReSharper disable JoinDeclarationAndInitializer
// ReSharper disable InlineOutVariableDeclaration
// ReSharper disable TooWideLocalVariableScope

namespace Aaru.Core.Devices.Dumping
{
    partial class Dump
    {
        void TrimCdUserData(ExtentsULong audioExtents, uint blockSize, DumpHardwareType currentTry,
                            ExtentsULong extents, bool newTrim, int offsetBytes, bool read6, bool read10, bool read12,
                            bool read16, bool readcd, int sectorsForOffset, uint subSize,
                            MmcSubchannel supportedSubchannel, bool supportsLongSectors, ref double totalDuration,
                            SubchannelLog subLog, MmcSubchannel desiredSubchannel, Track[] tracks,
                            Dictionary<byte, string> isrcs, ref string mcn)
        {
            DateTime          start;
            DateTime          end;
            bool              sense       = true; // Sense indicator
            byte[]            cmdBuf      = null; // Data buffer
            double            cmdDuration = 0;    // Command execution time
            const uint        sectorSize  = 2352; // Full sector size
            PlextorSubchannel supportedPlextorSubchannel;

            switch(supportedSubchannel)
            {
                case MmcSubchannel.None:
                    supportedPlextorSubchannel = PlextorSubchannel.None;

                    break;
                case MmcSubchannel.Raw:
                    supportedPlextorSubchannel = PlextorSubchannel.All;

                    break;
                case MmcSubchannel.Q16:
                    supportedPlextorSubchannel = PlextorSubchannel.Q16;

                    break;
                case MmcSubchannel.Rw:
                    supportedPlextorSubchannel = PlextorSubchannel.Pack;

                    break;
                default:
                    supportedPlextorSubchannel = PlextorSubchannel.None;

                    break;
            }

            if(_resume.BadBlocks.Count <= 0 ||
               _aborted                     ||
               !_trim                       ||
               !newTrim)
                return;

            start = DateTime.UtcNow;
            UpdateStatus?.Invoke("Trimming skipped sectors");
            _dumpLog.WriteLine("Trimming skipped sectors");

            ulong[] tmpArray = _resume.BadBlocks.ToArray();
            InitProgress?.Invoke();

            for(int b = 0; b < tmpArray.Length; b++)
            {
                ulong badSector = tmpArray[b];

                if(_aborted)
                {
                    currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                    UpdateStatus?.Invoke("Aborted!");
                    _dumpLog.WriteLine("Aborted!");

                    break;
                }

                PulseProgress?.Invoke($"Trimming sector {badSector}");

                Track track = tracks.OrderBy(t => t.TrackStartSector).
                                     LastOrDefault(t => badSector >= t.TrackStartSector);

                byte sectorsToTrim   = 1;
                uint badSectorToRead = (uint)badSector;

                if(_fixOffset                       &&
                   audioExtents.Contains(badSector) &&
                   offsetBytes != 0)
                {
                    if(offsetBytes > 0)
                    {
                        badSectorToRead -= (uint)sectorsForOffset;
                    }

                    sectorsToTrim = (byte)(sectorsForOffset + 1);
                }

                if(_supportsPlextorD8 && audioExtents.Contains(badSector))
                {
                    sense = ReadPlextorWithSubchannel(out cmdBuf, out _, badSectorToRead, blockSize, sectorsToTrim,
                                                      supportedPlextorSubchannel, out cmdDuration);

                    totalDuration += cmdDuration;
                }
                else if(readcd)
                    sense = _dev.ReadCd(out cmdBuf, out _, badSectorToRead, blockSize, sectorsToTrim,
                                        MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                        true, MmcErrorField.None, supportedSubchannel, _dev.Timeout, out cmdDuration);
                else if(read16)
                    sense = _dev.Read16(out cmdBuf, out _, 0, false, true, false, badSectorToRead, blockSize, 0,
                                        sectorsToTrim, false, _dev.Timeout, out cmdDuration);
                else if(read12)
                    sense = _dev.Read12(out cmdBuf, out _, 0, false, true, false, false, badSectorToRead, blockSize, 0,
                                        sectorsToTrim, false, _dev.Timeout, out cmdDuration);
                else if(read10)
                    sense = _dev.Read10(out cmdBuf, out _, 0, false, true, false, false, badSectorToRead, blockSize, 0,
                                        sectorsToTrim, _dev.Timeout, out cmdDuration);
                else if(read6)
                    sense = _dev.Read6(out cmdBuf, out _, badSectorToRead, blockSize, sectorsToTrim, _dev.Timeout,
                                       out cmdDuration);

                totalDuration += cmdDuration;

                if(sense || _dev.Error)
                    continue;

                if(!sense &&
                   !_dev.Error)
                {
                    _resume.BadBlocks.Remove(badSector);
                    extents.Add(badSector);
                }

                // Because one block has been partially used to fix the offset
                if(_fixOffset                       &&
                   audioExtents.Contains(badSector) &&
                   offsetBytes != 0)
                {
                    uint blocksToRead = sectorsToTrim;

                    FixOffsetData(offsetBytes, sectorSize, sectorsForOffset, supportedSubchannel, ref blocksToRead,
                                  subSize, ref cmdBuf, blockSize, false);
                }

                if(supportedSubchannel != MmcSubchannel.None)
                {
                    byte[] data = new byte[sectorSize];
                    byte[] sub  = new byte[subSize];
                    Array.Copy(cmdBuf, 0, data, 0, sectorSize);
                    Array.Copy(cmdBuf, sectorSize, sub, 0, subSize);
                    _outputPlugin.WriteSectorLong(data, badSector);

                    bool indexesChanged = WriteSubchannelToImage(supportedSubchannel, desiredSubchannel, sub, badSector,
                                                                 1, subLog, isrcs, (byte)track.TrackSequence, ref mcn,
                                                                 tracks);

                    // Set tracks and go back
                    if(!indexesChanged)
                        continue;

                    (_outputPlugin as IWritableOpticalImage).SetTracks(tracks.ToList());
                    b--;

                    continue;
                }

                if(supportsLongSectors)
                    _outputPlugin.WriteSectorLong(cmdBuf, badSector);
                else
                {
                    if(cmdBuf.Length % sectorSize == 0)
                    {
                        byte[] data = new byte[2048];
                        Array.Copy(cmdBuf, 16, data, 0, 2048);

                        _outputPlugin.WriteSector(data, badSector);
                    }
                    else
                        _outputPlugin.WriteSectorLong(cmdBuf, badSector);
                }
            }

            EndProgress?.Invoke();
            end = DateTime.UtcNow;
            UpdateStatus?.Invoke($"Trimming finished in {(end - start).TotalSeconds} seconds.");
            _dumpLog.WriteLine("Trimming finished in {0} seconds.", (end - start).TotalSeconds);
        }
    }
}