﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LiteDB
{
    /// <summary>
    /// Implement a locker service locking datafile to shared/reserved and exclusive access mode
    /// Implement both thread lock and process lock
    /// Shared -> Reserved -> Exclusive => !Reserved => !Shared
    /// Reserved -> Exclusive => !Reserved
    /// [Thread Safe]
    /// </summary>
    public class LockService
    {
        #region Properties + Ctor

        private TimeSpan _timeout;
        private IDiskService _disk;
        private CacheService _cache;
        private Logger _log;
        private LockState _state;
        private bool _shared = false;
        private ReaderWriterLockSlim _thread = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

        internal LockService(IDiskService disk, CacheService cache, TimeSpan timeout, Logger log)
        {
            _disk = disk;
            _cache = cache;
            _log = log;
            _timeout = timeout;
            _state = LockState.Unlocked;
        }

        /// <summary>
        /// Get current datafile lock state
        /// </summary>
        public LockState State { get { return _state; } }

        #endregion

        #region Public Methods

        /// <summary>
        /// Enter in Shared lock mode.
        /// </summary>
        public LockControl Shared()
        {
            var read = this.ThreadRead();
            var shared = this.LockShared();

            return new LockControl(() =>
            {
                shared();
                read();
            });
        }


        /// <summary>
        /// Enter in Reserved lock mode.
        /// </summary>
        public LockControl Reserved()
        {
            var write = this.ThreadWrite();
            var reserved = this.LockReserved();

            return new LockControl(() =>
            {
                reserved();
                write();
            });
        }

        /// <summary>
        /// Enter in Exclusive lock mode
        /// </summary>
        public LockControl Exclusive()
        {
            var exclusive = this.LockExclusive();

            return new LockControl(exclusive);
        }

        #endregion

        #region Process lock control

        /// <summary>
        /// Try enter in shared lock (read) - Call action if request a new lock
        /// </summary>
        private Action LockShared()
        {
            lock (_disk)
            {
                if (_state != LockState.Unlocked) return () => { };

                _log.Write(Logger.DISK, "enter in shared lock mode");

                _disk.Lock(LockState.Shared, _timeout);

                _state = LockState.Shared;
                _shared = true;

                this.AvoidDirtyRead();

                return () =>
                {
                    _log.Write(Logger.DISK, "exit shared lock mode");

                    _shared = false;
                    _state = LockState.Unlocked;

                    _disk.Unlock(LockState.Shared);
                };
            }
        }

        /// <summary>
        /// Try enter in reserved mode (read - single reserved)
        /// </summary>
        private Action LockReserved()
        {
            lock (_disk)
            {
                if (_state == LockState.Reserved) return () => { };

                _log.Write(Logger.DISK, "enter in reserved lock mode");

                _disk.Lock(LockState.Reserved, _timeout);

                _state = LockState.Reserved;

                // can be a new lock, calls action to notifify
                if (!_shared)
                {
                    this.AvoidDirtyRead();
                }

                // is new lock only when not came from a shared lock
                return () =>
                {
                    _log.Write(Logger.DISK, "exit in reserved lock mode");

                    _state = _shared ? LockState.Shared : LockState.Unlocked;
                    _disk.Unlock(LockState.Reserved);
                };
            }
        }

        /// <summary>
        /// Try enter in exclusive mode (single write)
        /// </summary>
        private Action LockExclusive()
        {
            lock (_disk)
            {
                if (_state != LockState.Reserved) throw new InvalidOperationException("Lock state must be reserved");

                // has a shared lock? unlock first (will keep reserved lock)
                if (_shared)
                {
                    _disk.Unlock(LockState.Shared);
                }

                _log.Write(Logger.DISK, "enter in exclusive lock mode");

                _disk.Lock(LockState.Exclusive, _timeout);
                _state = LockState.Exclusive;

                return () =>
                {
                    _log.Write(Logger.DISK, "exit in exclusive lock mode");
                    _state = LockState.Reserved;
                    _disk.Unlock(LockState.Exclusive);

                    // if was in a shared lock before exclusive lock, back to shared again (still reserved lock)
                    if (_shared)
                    {
                        _disk.Lock(LockState.Shared, _timeout);
                    }
                };
            }
        }

        #endregion

        #region Thread lock control

        /// <summary>
        /// Start new shared read lock control using timeout
        /// </summary>
        private Action ThreadRead()
        {
            // if current thread are in read mode, do nothing
            if (_thread.IsReadLockHeld || _thread.IsWriteLockHeld) return () => { };

            // try enter in read mode
            _thread.TryEnterReadLock(_timeout);

            // when dispose, close read mode
            return _thread.ExitReadLock;
        }

        /// <summary>
        /// Start new exclusive write lock control using timeout
        /// </summary>
        private Action ThreadWrite()
        {
            // if current thread is already in write mode, do nothing
            if (_thread.IsWriteLockHeld) return () => { };

            // if current thread is in read mode, exit read mode first
            if (_thread.IsReadLockHeld)
            {
                _thread.ExitReadLock();
                _thread.TryEnterWriteLock(_timeout);

                // when dispose write mode, enter again in read mode
                return () =>
                {
                    _thread.ExitWriteLock();
                    _thread.TryEnterReadLock(_timeout);
                };
            }

            // try enter in write mode
            _thread.TryEnterWriteLock(_timeout);

            // and release when dispose
            return _thread.ExitWriteLock;
        }

        #endregion

        /// <summary>
        /// Test if cache still valid (if datafile was changed by another process reset cache)
        /// </summary>
        private void AvoidDirtyRead()
        {
            // if disk are exclusive don't need check dirty read
            if (_disk.IsExclusive) return;

            _log.Write(Logger.CACHE, "checking disk to avoid dirty read");

            // empty cache? just exit
            if (_cache.CleanUsed == 0) return;

            // get ChangeID from cache
            var header = _cache.GetPage(0) as HeaderPage;
            var changeID = header == null ? 0 : header.ChangeID;

            // and get header from disk
            var disk = BasePage.ReadPage(_disk.ReadPage(0)) as HeaderPage;

            // if header change, clear cache and add new header to cache
            if (disk.ChangeID != changeID)
            {
                _log.Write(Logger.CACHE, "file changed from another process");

                _cache.ClearPages();
                _cache.AddPage(disk);
            }
        }
    }
}