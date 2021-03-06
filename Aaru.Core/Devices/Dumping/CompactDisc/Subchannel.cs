// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Subchannel.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : CompactDisc dumping.
//
// --[ Description ] ----------------------------------------------------------
//
//     Handles CompactDisc subchannel data.
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
using Aaru.Checksums;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Structs;
using Aaru.Core.Logging;
using Aaru.Decoders.CD;
using Aaru.Devices;

// ReSharper disable JoinDeclarationAndInitializer
// ReSharper disable InlineOutVariableDeclaration
// ReSharper disable TooWideLocalVariableScope

namespace Aaru.Core.Devices.Dumping
{
    partial class Dump
    {
        public static bool SupportsRwSubchannel(Device dev, DumpLog dumpLog, UpdateStatusHandler updateStatus)
        {
            dumpLog?.WriteLine("Checking if drive supports full raw subchannel reading...");
            updateStatus?.Invoke("Checking if drive supports full raw subchannel reading...");

            return !dev.ReadCd(out _, out _, 0, 2352 + 96, 1, MmcSectorTypes.AllTypes, false, false, true,
                               MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, MmcSubchannel.Raw,
                               dev.Timeout, out _);
        }

        public static bool SupportsPqSubchannel(Device dev, DumpLog dumpLog, UpdateStatusHandler updateStatus)
        {
            dumpLog?.WriteLine("Checking if drive supports PQ subchannel reading...");
            updateStatus?.Invoke("Checking if drive supports PQ subchannel reading...");

            return !dev.ReadCd(out _, out _, 0, 2352 + 16, 1, MmcSectorTypes.AllTypes, false, false, true,
                               MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, MmcSubchannel.Q16,
                               dev.Timeout, out _);
        }

        // Return true if indexes have changed
        bool WriteSubchannelToImage(MmcSubchannel supportedSubchannel, MmcSubchannel desiredSubchannel, byte[] sub,
                                    ulong sectorAddress, uint length, SubchannelLog subLog,
                                    Dictionary<byte, string> isrcs, byte currentTrack, ref string mcn, Track[] tracks)
        {
            if(supportedSubchannel == MmcSubchannel.Q16)
                sub = Subchannel.ConvertQToRaw(sub);

            if(desiredSubchannel != MmcSubchannel.None)
                _outputPlugin.WriteSectorsTag(sub, sectorAddress, length, SectorTagType.CdSectorSubchannel);

            subLog?.WriteEntry(sub, supportedSubchannel == MmcSubchannel.Raw, (long)sectorAddress, length);

            byte[] deSub = Subchannel.Deinterleave(sub);

            // Check subchannel
            for(int subPos = 0; subPos < deSub.Length; subPos += 96)
            {
                byte[] q = new byte[12];
                Array.Copy(deSub, subPos + 12, q, 0, 12);

                CRC16CCITTContext.Data(q, 10, out byte[] crc);
                bool crcOk = crc[0] == q[10] && crc[1] == q[11];

                // ISRC
                if((q[0] & 0x3) == 3)
                {
                    string isrc = Subchannel.DecodeIsrc(q);

                    if(isrc == null ||
                       isrc == "000000000000")
                        continue;

                    if(!crcOk)
                        continue;

                    if(!isrcs.ContainsKey(currentTrack))
                    {
                        _dumpLog?.WriteLine($"Found new ISRC {isrc} for track {currentTrack}.");
                        UpdateStatus?.Invoke($"Found new ISRC {isrc} for track {currentTrack}.");
                    }
                    else if(isrcs[currentTrack] != isrc)
                    {
                        _dumpLog?.
                            WriteLine($"ISRC for track {currentTrack} changed from {isrcs[currentTrack]} to {isrc}.");

                        UpdateStatus?.
                            Invoke($"ISRC for track {currentTrack} changed from {isrcs[currentTrack]} to {isrc}.");
                    }

                    isrcs[currentTrack] = isrc;
                }
                else if((q[0] & 0x3) == 2)
                {
                    string newMcn = Subchannel.DecodeMcn(q);

                    if(newMcn == null ||
                       newMcn == "0000000000000")
                        continue;

                    if(!crcOk)
                        continue;

                    if(mcn is null)
                    {
                        _dumpLog?.WriteLine($"Found new MCN {newMcn}.");
                        UpdateStatus?.Invoke($"Found new MCN {newMcn}.");
                    }
                    else if(mcn != newMcn)
                    {
                        _dumpLog?.WriteLine($"MCN changed from {mcn} to {newMcn}.");
                        UpdateStatus?.Invoke($"MCN changed from {mcn} to {newMcn}.");
                    }

                    mcn = newMcn;
                }
                else if((q[0] & 0x3) == 1)
                {
                    // TODO: Indexes

                    // Pregap
                    if(q[2] != 0)
                        continue;

                    if(!crcOk)
                        continue;

                    byte trackNo = (byte)(((q[1] / 16) * 10) + (q[1] & 0x0F));

                    for(int i = 0; i < tracks.Length; i++)
                    {
                        if(tracks[i].TrackSequence != trackNo ||
                           trackNo                 == 1)
                        {
                            continue;
                        }

                        byte pmin   = (byte)(((q[3] / 16) * 10) + (q[3] & 0x0F));
                        byte psec   = (byte)(((q[4] / 16) * 10) + (q[4] & 0x0F));
                        byte pframe = (byte)(((q[5] / 16) * 10) + (q[5] & 0x0F));
                        int  qPos   = (pmin * 60 * 75) + (psec * 75) + pframe;

                        if(tracks[i].TrackPregap >= (ulong)(qPos + 1))
                            continue;

                        tracks[i].TrackPregap      =  (ulong)(qPos + 1);
                        tracks[i].TrackStartSector -= tracks[i].TrackPregap;

                        if(i > 0)
                            tracks[i - 1].TrackEndSector = tracks[i].TrackStartSector - 1;

                        _dumpLog?.WriteLine($"Pregap for track {trackNo} set to {tracks[i].TrackPregap} sectors.");
                        UpdateStatus?.Invoke($"Pregap for track {trackNo} set to {tracks[i].TrackPregap} sectors.");

                        return true;
                    }
                }
            }

            return false;
        }
    }
}