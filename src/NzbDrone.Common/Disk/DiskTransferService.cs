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
        TransferMode TransferFolder(String sourcePath, String targetPath, TransferMode mode);
        TransferMode TransferFolderVerified(String sourcePath, String targetPath, TransferMode mode);
        TransferMode TransferFile(String sourcePath, String targetPath, TransferMode mode, bool overwrite = false);
        TransferMode TransferFileVerified(String sourcePath, String targetPath, TransferMode mode);
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

        public TransferMode TransferFolder(String sourcePath, String targetPath, TransferMode mode)
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
                result &= TransferFolder(subDir.FullName, Path.Combine(targetPath, subDir.Name), mode);
            }

            foreach (var sourceFile in _diskProvider.GetFileInfos(sourcePath))
            {
                var destFile = Path.Combine(targetPath, sourceFile.Name);

                result &= TransferFile(sourceFile.FullName, destFile, mode, true);
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                _diskProvider.DeleteFolder(sourcePath, true);
            }

            return result;
        }

        public TransferMode TransferFolderVerified(String sourcePath, String targetPath, TransferMode mode)
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
                result &= TransferFolderVerified(subDir.FullName, Path.Combine(targetPath, subDir.Name), mode);
            }

            foreach (var sourceFile in _diskProvider.GetFileInfos(sourcePath))
            {
                var destFile = Path.Combine(targetPath, sourceFile.Name);

                result &= TransferFileVerified(sourceFile.FullName, destFile, mode);
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                _diskProvider.DeleteFolder(sourcePath, true);
            }

            return result;
        }

        
        public TransferMode TransferFile(String sourcePath, String targetPath, TransferMode mode, bool overwrite = false)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath();
            Ensure.That(targetPath, () => targetPath).IsValidPath();

            _logger.Debug("{0} [{1}] > [{2}]", mode, sourcePath, targetPath);

            if (sourcePath.PathEquals(targetPath))
            {
                _logger.Warn("Source and destination can't be the same {0}", sourcePath);
                return TransferMode.None;
            }

            if (_diskProvider.FileExists(targetPath) && overwrite)
            {
                _diskProvider.DeleteFile(targetPath);
            }

            if (mode.HasFlag(TransferMode.HardLink))
            {
                bool createdHardlink = _diskProvider.TryCreateHardLink(sourcePath, targetPath);
                if (createdHardlink)
                {
                    return TransferMode.HardLink;
                }
                if (!mode.HasFlag(TransferMode.Copy))
                {
                    throw new IOException("Hardlinking from '" + sourcePath + "' to '" + targetPath + "' failed.");
                }
            }

            if (mode.HasFlag(TransferMode.Copy))
            {
                _diskProvider.CopySingleFile(sourcePath, targetPath);
                return TransferMode.Copy;
            }

            if (mode.HasFlag(TransferMode.Move))
            {
                _diskProvider.MoveSingleFile(sourcePath, targetPath);
                return TransferMode.Move;
            }

            return TransferMode.None;
        }

        public TransferMode TransferFileVerified(String sourcePath, String targetPath, TransferMode mode)
        {
            Ensure.That(sourcePath, () => sourcePath).IsValidPath();
            Ensure.That(targetPath, () => targetPath).IsValidPath();

            _logger.Debug("{0}Verified [{1}] > [{2}]", mode, sourcePath, targetPath);

            if (sourcePath.PathEquals(targetPath))
            {
                _logger.Warn("Source and destination can't be the same {0}", sourcePath);
                return TransferMode.None;
            }

            if (mode.HasFlag(TransferMode.HardLink))
            {
                if (_diskProvider.TryCreateHardLink(sourcePath, targetPath))
                {
                    return TransferMode.HardLink;
                }
            }

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

        private Boolean TryCopyFile(String sourcePath, String targetPath)
        {
            var originalSize = _diskProvider.GetFileSize(sourcePath);

            for (var i = 0; i <= RetryCount; i++)
            {
                _diskProvider.CopySingleFile(sourcePath, targetPath);

                var targetSize = _diskProvider.GetFileSize(targetPath);

                if (targetSize == originalSize)
                {
                    return true;
                }

                _diskProvider.DeleteFile(targetPath);

                if (i == RetryCount)
                {
                    _logger.Error("Failed to completely transfer [{0}] to [{1}], aborting.", sourcePath, targetPath, i + 1, RetryCount);
                }
                else if (i == 0)
                {
                    _logger.Warn("Failed to completely transfer [{0}] to [{1}], retrying [{2}/{3}].", sourcePath, targetPath, i + 1, RetryCount);
                }
                else
                {
                    _logger.Debug("Failed to completely transfer [{0}] to [{1}], retrying [{2}/{3}].", sourcePath, targetPath, i + 1, RetryCount);
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
                    _diskProvider.MoveSingleFile(backupPath, targetPath);

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
