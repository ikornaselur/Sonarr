﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Common.EnsureThat;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Disk
{
    public interface IDiskTransferService
    {
        TransferMode TransferFolder(String sourcePath, String targetPath, TransferMode mode, bool verified = true);
        TransferMode TransferFile(String sourcePath, String targetPath, TransferMode mode, bool overwrite = false, bool verified = true);
    }

    public class DiskTransferService : IDiskTransferService
    {
        private const Int32 RetryCount = 2;

        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public DiskTransferService(IDiskProvider diskProvider, Logger logger)
        {
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public TransferMode TransferFolder(String sourcePath, String targetPath, TransferMode mode, bool verified = true)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath();
            Ensure.That(targetPath, () => targetPath).IsValidPath();

            if (!_diskProvider.FolderExists(targetPath))
            {
                _diskProvider.CreateFolder(targetPath);
            }

            var result = mode;

            foreach (var subDir in _diskProvider.GetDirectoryInfos(sourcePath))
            {
                result &= TransferFolder(subDir.FullName, Path.Combine(targetPath, subDir.Name), mode, verified);
            }

            foreach (var sourceFile in _diskProvider.GetFileInfos(sourcePath))
            {
                var destFile = Path.Combine(targetPath, sourceFile.Name);

                result &= TransferFile(sourceFile.FullName, destFile, mode, true, verified);
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                _diskProvider.DeleteFolder(sourcePath, true);
            }

            return result;
        }

        public TransferMode TransferFile(String sourcePath, String targetPath, TransferMode mode, bool overwrite = false, bool verified = true)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath();
            Ensure.That(targetPath, () => targetPath).IsValidPath();

            _logger.Debug("{0} [{1}] > [{2}]", mode, sourcePath, targetPath);

            if (sourcePath.PathEquals(targetPath))
            {
                throw new IOException(string.Format("Source and destination can't be the same {0}", sourcePath));
            }

            if (sourcePath.IsParentPath(targetPath))
            {
                throw new IOException(string.Format("Destination cannot be a child of the source [{0}] => [{1}]", sourcePath, targetPath));
            }

            if (_diskProvider.FileExists(targetPath) && overwrite)
            {
                _diskProvider.DeleteFile(targetPath);
            }

            if (mode.HasFlag(TransferMode.HardLink))
            {
                var createdHardlink = _diskProvider.TryCreateHardLink(sourcePath, targetPath);
                if (createdHardlink)
                {
                    return TransferMode.HardLink;
                }
                if (!mode.HasFlag(TransferMode.Copy))
                {
                    throw new IOException("Hardlinking from '" + sourcePath + "' to '" + targetPath + "' failed.");
                }
            }

            if (verified)
            {
                if (mode.HasFlag(TransferMode.Copy))
                {
                    if (TryCopyFile(sourcePath, targetPath))
                    {
                        return TransferMode.Copy;
                    }
                }

                if (mode.HasFlag(TransferMode.Move))
                {
                    if (TryMoveFile(sourcePath, targetPath))
                    {
                        return TransferMode.Move;
                    }
                }

                throw new IOException(String.Format("Failed to completely transfer [{0}] to [{1}], aborting.", sourcePath, targetPath));
            }
            else
            {
                if (mode.HasFlag(TransferMode.Copy))
                {
                    _diskProvider.CopyFile(sourcePath, targetPath);
                    return TransferMode.Copy;
                }

                if (mode.HasFlag(TransferMode.Move))
                {
                    _diskProvider.MoveFile(sourcePath, targetPath);
                    return TransferMode.Move;
                }
            }

            return TransferMode.None;
        }

        private Boolean TryCopyFile(String sourcePath, String targetPath)
        {
            var originalSize = _diskProvider.GetFileSize(sourcePath);

            for (var i = 0; i <= RetryCount; i++)
            {
                _diskProvider.CopyFile(sourcePath, targetPath);

                if (_diskProvider.FileExists(targetPath))
                {
                    var targetSize = _diskProvider.GetFileSize(targetPath);

                    if (targetSize == originalSize)
                    {
                        return true;
                    }
                }

                _diskProvider.DeleteFile(targetPath);

                if (i == RetryCount)
                {
                    _logger.Error("Failed to completely transfer [{0}] to [{1}], aborting.", sourcePath, targetPath, i + 1, RetryCount);
                }
                else
                {
                    _logger.Warn("Failed to completely transfer [{0}] to [{1}], retrying [{2}/{3}].", sourcePath, targetPath, i + 1, RetryCount);
                }
            }

            return false;
        }

        private Boolean TryMoveFile(String sourcePath, String targetPath)
        {
            var originalSize = _diskProvider.GetFileSize(sourcePath);

            var backupPath = sourcePath + ".movebackup";

            if (_diskProvider.FileExists(backupPath))
            {
                _logger.Trace("Removing old backup.");
                _diskProvider.DeleteFile(backupPath);
            }

            try
            {
                _logger.Trace("Attempting to move hardlinked backup.");
                if (_diskProvider.TryCreateHardLink(sourcePath, backupPath))
                {
                    _diskProvider.MoveFile(backupPath, targetPath);

                    var targetSize = _diskProvider.GetFileSize(targetPath);

                    if (targetSize == originalSize)
                    {
                        _logger.Trace("Hardlink move succeeded, deleting source.");
                        _diskProvider.DeleteFile(sourcePath);
                        return true;
                    }
                }
            }
            finally
            {
                if (_diskProvider.FileExists(backupPath))
                {
                    _diskProvider.DeleteFile(backupPath);
                }
            }

            _logger.Trace("Hardlink move failed, reverting to copy.");
            if (TryCopyFile(sourcePath, targetPath))
            {
                _logger.Trace("Copy succeeded, deleting source.");
                _diskProvider.DeleteFile(sourcePath);
                return true;
            }

            _logger.Trace("Copy failed.");
            return false;
        }
    }
}
