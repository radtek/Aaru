// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : File.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : FATX filesystem plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Methods to handle files.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2019 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.Linq;
using DiscImageChef.CommonTypes.Structs;

namespace DiscImageChef.Filesystems.FATX
{
    public partial class XboxFatPlugin
    {
        public Errno MapBlock(string path, long fileBlock, out long deviceBlock)
        {
            deviceBlock = 0;
            if(!mounted) return Errno.AccessDenied;

            throw new NotImplementedException();
        }

        public Errno GetAttributes(string path, out FileAttributes attributes)
        {
            attributes = new FileAttributes();
            if(!mounted) return Errno.AccessDenied;

            throw new NotImplementedException();
        }

        public Errno Read(string path, long offset, long size, ref byte[] buf)
        {
            if(!mounted) return Errno.AccessDenied;

            throw new NotImplementedException();
        }

        public Errno Stat(string path, out FileEntryInfo stat)
        {
            stat = null;
            if(!mounted) return Errno.AccessDenied;

            if(debug && (string.IsNullOrEmpty(path) || path == "$" || path == "/"))
            {
                stat = new FileEntryInfo
                {
                    Attributes = FileAttributes.Directory | FileAttributes.System | FileAttributes.Hidden,
                    Blocks     = GetClusters(superblock.rootDirectoryCluster).Length,
                    BlockSize  = bytesPerCluster,
                    DeviceNo   = 0,
                    GID        = 0,
                    Length     = GetClusters(superblock.rootDirectoryCluster).Length * bytesPerCluster,
                    Inode      = superblock.rootDirectoryCluster,
                    Links      = 1,
                    Mode       = 0x16D,
                    UID        = 0
                };

                return Errno.NoError;
            }

            Errno err = GetFileEntry(path, out DirectoryEntry entry);
            if(err != Errno.NoError) return err;

            stat = new FileEntryInfo
            {
                Attributes   = new FileAttributes(),
                Blocks       = entry.length / bytesPerCluster,
                BlockSize    = bytesPerCluster,
                DeviceNo     = 0,
                GID          = 0,
                Length       = entry.length,
                Inode        = entry.firstCluster,
                Links        = 1,
                Mode         = (uint)(entry.attributes.HasFlag(Attributes.Directory) ? 0x16D : 0x124),
                UID          = 0,
                CreationTime = DateHandlers.DosToDateTime(entry.creationDate, entry.creationTime).AddYears(20),
                AccessTime   = DateHandlers.DosToDateTime(entry.lastAccessDate, entry.lastAccessTime).AddYears(20),
                LastWriteTime = DateHandlers.DosToDateTime(entry.lastWrittenDate, entry.lastWrittenTime)
                                            .AddYears(20)
            };

            if(entry.attributes.HasFlag(Attributes.Directory))
            {
                stat.Attributes |= FileAttributes.Directory;
                stat.Blocks     =  GetClusters(entry.firstCluster).Length;
                stat.Length     =  stat.Blocks * stat.BlockSize;
            }

            if(entry.attributes.HasFlag(Attributes.ReadOnly)) stat.Attributes |= FileAttributes.ReadOnly;
            if(entry.attributes.HasFlag(Attributes.Hidden)) stat.Attributes   |= FileAttributes.Hidden;
            if(entry.attributes.HasFlag(Attributes.System)) stat.Attributes   |= FileAttributes.System;
            if(entry.attributes.HasFlag(Attributes.Archive)) stat.Attributes  |= FileAttributes.Archive;

            return Errno.NoError;
        }

        uint[] GetClusters(uint startCluster)
        {
            if(startCluster == 0) return null;

            if(fat16 is null)
            {
                if(startCluster >= fat32.Length) return null;
            }
            else if(startCluster >= fat16.Length) return null;

            List<uint> clusters = new List<uint>();

            uint nextCluster = startCluster;

            if(fat16 is null)
                while((nextCluster & FAT32_MASK) > 0 && (nextCluster & FAT32_MASK) <= FAT32_BAD)
                {
                    clusters.Add(nextCluster);
                    nextCluster = fat32[nextCluster];
                }
            else
                while(nextCluster > 0 && nextCluster <= FAT16_BAD)
                {
                    clusters.Add(nextCluster);
                    nextCluster = fat16[nextCluster];
                }

            return clusters.ToArray();
        }

        Errno GetFileEntry(string path, out DirectoryEntry entry)
        {
            entry = new DirectoryEntry();

            string cutPath =
                path.StartsWith("/") ? path.Substring(0).ToLower(cultureInfo) : path.ToLower(cultureInfo);
            string[] pieces = cutPath.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);

            if(pieces.Length == 0) return Errno.InvalidArgument;

            string parentPath = string.Join("/", pieces, 0, pieces.Length - 1);

            Errno err = ReadDir(parentPath, out _);

            if(err != Errno.NoError) return err;

            Dictionary<string, DirectoryEntry> parent;

            if(pieces.Length == 1) parent = rootDirectory;
            else if(!directoryCache.TryGetValue(parentPath, out parent)) return Errno.InvalidArgument;

            KeyValuePair<string, DirectoryEntry> dirent =
                parent.FirstOrDefault(t => t.Key.ToLower(cultureInfo) == pieces[pieces.Length - 1]);

            if(string.IsNullOrEmpty(dirent.Key)) return Errno.NoSuchFile;

            entry = dirent.Value;
            return Errno.NoError;
        }
    }
}