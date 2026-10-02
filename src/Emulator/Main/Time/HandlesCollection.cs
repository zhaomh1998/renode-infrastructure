//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Time
{
    /// <summary>
    /// Represents a collection of time handles and allows to iterate over it in an optimal way.
    /// </summary>
    /// <remarks>
    /// It keeps track of handles not ready for a new time grant in a separate list so they can be accessed faster.
    /// If there are any not ready time handles it will iterate only over them.
    /// </remarks>
    public sealed class HandlesCollection
    {
        /// <summary>
        /// Creates new empty collection.
        /// </summary>
        public HandlesCollection()
        {
            Ready = new LinkedList<TimeHandle>();
            NotReady = new LinkedList<TimeHandle>();
            registrationIndex = new Dictionary<TimeHandle, ulong>();
            locker = new object();
        }

        /// <summary>
        /// Executes `Latch` method on all handles and removes the disposed ones from the collection.
        /// </summary>
        public void LatchAllAndCollectGarbage()
        {
            var wasLocked = false;
            try
            {
                InnerLatchAndCollectGarbage(ref wasLocked, NotReady);
                InnerLatchAndCollectGarbage(ref wasLocked, Ready);
            }
            finally
            {
                if(wasLocked)
                {
                    Monitor.Exit(locker);
                }
            }
        }

        /// <summary>
        /// Executes `Unlatch` method on all handles.
        /// </summary>
        public void UnlatchAll()
        {
            foreach(var h in All)
            {
                h.Unlatch();
            }

            // After unlatch some handles might be disabled.
            // Disabling not ready handles make them ready.
            // We need to update list to reflect changes.
            foreach(var node in NotReady.Nodes())
            {
                UpdateHandle(node);
            }
        }

        /// <summary>
        /// Adds new handle to the collection.
        /// </summary>
        /// <remarks>
        /// Depending of the value of <see cref="IsReadyForNewTimeGrant">, the handle is put either in <see cref="Ready"> or <see cref="NotReady"> queue.
        /// </remarks>
        public void Add(TimeHandle handle)
        {
            lock(locker)
            {
                registrationIndex[handle] = nextRegistrationIndex++;
                var readyForGrant = handle.IsReadyForNewTimeGrant;
                (readyForGrant ? Ready : NotReady).AddLast(handle);
                handle.IsDone = readyForGrant;
            }
        }

        /// <summary>
        /// Updates state of a the handle by moving it to a proper queue.
        /// </summary>
        public void UpdateHandle(LinkedListNode<TimeHandle> handle)
        {
            var isOnReadyList = handle.List == Ready;
            if(handle.Value.IsReadyForNewTimeGrant == isOnReadyList)
            {
                return;
            }

            lock(locker)
            {
                // Both queues keep registration order, so a handle that blocked once does not move behind
                // the others: under serial execution this order is the order in which sinks run each quantum.
                if(isOnReadyList)
                {
                    Ready.Remove(handle);
                    InsertInRegistrationOrder(NotReady, handle);
                    handle.Value.IsDone = false;
                }
                else
                {
                    NotReady.Remove(handle);
                    InsertInRegistrationOrder(Ready, handle);
                    handle.Value.IsDone = true;
                }
            }
        }

        /// <summary>
        /// Calculates a base elapsed virtual time (minimal) for all handles.
        /// Returns `false` if the handles collection is empty.
        /// </summary>
        /// <param name="commonElapsedTime">Minimal elapsed virtual time</param>
        /// <param name="blockers">Number of handles that are blocking common elapsed time progression (number of handles with minimal elapsed virtual time)</param>
        public bool TryGetCommonElapsedTime(out TimeInterval commonElapsedTime, out uint blockers)
        {
            lock(locker)
            {
                if(NotReady.Count == 0 && Ready.Count == 0)
                {
                    blockers = 0;
                    commonElapsedTime = TimeInterval.Empty;
                    return false;
                }

                var (notReadyMinTicks, notReadMinTicksCount) = GetMinTicksAndCountOccurences(NotReady);
                var (readyMinTicks, readyMinTicksCount) = GetMinTicksAndCountOccurences(Ready);

                if(notReadyMinTicks < readyMinTicks)
                {
                    commonElapsedTime = TimeInterval.FromTicks(notReadyMinTicks);
                    blockers = notReadMinTicksCount;
                }
                else
                {
                    commonElapsedTime = TimeInterval.FromTicks(readyMinTicks);

                    if(notReadMinTicksCount == readyMinTicks)
                    {
                        blockers = notReadMinTicksCount + readyMinTicksCount;
                    }
                    else
                    {
                        blockers = readyMinTicksCount;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Returns an enumeration of all handles in the collection - not-ready queue concatenated with ready one.
        /// </summary>
        public IEnumerable<TimeHandle> All { get { return NotReady.Concat(Ready); } }

        public LinkedList<TimeHandle> Ready { get; private set; }

        public LinkedList<TimeHandle> NotReady { get; private set; }

        private void InnerLatchAndCollectGarbage(ref bool wasLocked, LinkedList<TimeHandle> list)
        {
            foreach(var node in list.Nodes())
            {
                node.Value.Latch();

                if(node.Value.DetachRequested)
                {
                    if(!wasLocked)
                    {
                        Monitor.Enter(locker, ref wasLocked);
                    }
                    list.Remove(node);
                    registrationIndex.Remove(node.Value);
                    node.Value.Unlatch();
                }
            }
        }

        private void InsertInRegistrationOrder(LinkedList<TimeHandle> list, LinkedListNode<TimeHandle> handle)
        {
            var index = registrationIndex[handle.Value];
            var next = list.First;
            while(next != null && registrationIndex[next.Value] < index)
            {
                next = next.Next;
            }
            if(next == null)
            {
                list.AddLast(handle);
            }
            else
            {
                list.AddBefore(next, handle);
            }
        }

        private (ulong, uint) GetMinTicksAndCountOccurences(LinkedList<TimeHandle> list)
        {
            var minTicks = ulong.MaxValue;
            var count = 0u;

            foreach(var handle in list)
            {
                var ticks = handle.TotalElapsedTime.Ticks;

                if(ticks < minTicks)
                {
                    minTicks = ticks;
                    count = 1;
                }
                else if(ticks == minTicks)
                {
                    count++;
                }
            }

            return (minTicks, count);
        }

        private ulong nextRegistrationIndex;

        private readonly Dictionary<TimeHandle, ulong> registrationIndex;
        private readonly object locker;
    }
}