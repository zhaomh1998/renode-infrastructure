//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class NRF52840_PPI : BasicDoubleWordPeripheral, IKnownSize
    {
        public NRF52840_PPI(IMachine machine) : base(machine)
        {
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            for(var i = 0; i < ChannelGroups; i++)
            {
                channelGroupEnabled[i] = false;
            }
            for(var i = 0; i < ConfigurableChannels; i++)
            {
                eventEndpoint[i] = 0;
                taskEndpoint[i] = 0;
            }
            for(var i = 0; i < Channels; i++)
            {
                forkEndpoint[i] = 0;
                eventProviders[i] = null;
            }
            foreach(var callback in eventCallbacks)
            {
                callback.Key.EventTriggered -= callback.Value;
            }
            eventCallbacks.Clear();
            initialized = false;
        }

        public override void WriteDoubleWord(long offset, uint value)
        {
            if(!initialized)
            {
                DefinePreprogrammedChannels();
                initialized = true;
            }
            base.WriteDoubleWord(offset, value);
        }

        public long Size => 0x1000;

        /// <summary>
        /// Raises events that happen at the same time: those that `raise` triggers reach the channels together, in one
        /// round, once it returns.
        /// </summary>
        public void RaiseTogether(Action raise)
        {
            if(dispatchingEvents)
            {
                // they join the next round of the dispatch under way
                raise();
                return;
            }
            dispatchingEvents = true;
            try
            {
                raise();
                DispatchRaisedEvents();
            }
            finally
            {
                raisedEvents.Clear();
                dispatchingEvents = false;
            }
        }

        private void DefineRegisters()
        {
            for(var i = 0; i < ChannelGroups; i++)
            {
                var j = i;
                ((Registers)((int)Registers.ChannelGroup0Enable + i * 8)).Define(this, name: "TASKS_CHG[n].EN")
                    .WithFlag(0, FieldMode.Write, name: "EN", writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            channelGroupEnabled[j] = true;
                        }
                    })
                ;

                ((Registers)((int)Registers.ChannelGroup0Disable + i * 8)).Define(this, name: "TASKS_CHG[n].DIS")
                    .WithFlag(0, FieldMode.Write, name: "DIS", writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            channelGroupEnabled[j] = false;
                        }
                    })
                ;
            }

            Registers.ChannelEnable.Define(this, name: "CHEN")
                .WithFlags(0, 32, out channelEnabled, name: "CH[i]")
            ;

            Registers.ChannelEnableSet.Define(this, name: "CHENSET")
                .WithFlags(0, 32, name: "CH[i]", writeCallback: (i, _, value) =>
                {
                    if(value)
                    {
                        this.Log(LogLevel.Noisy, "PPI enable channel {0}", i);
                        channelEnabled[i].Value = true;
                    }
                }, valueProviderCallback: (i, _) => channelEnabled[i].Value)
            ;

            Registers.ChannelEnableClear.Define(this, name: "CHENCLR")
                .WithFlags(0, 32, name: "CH[i]", writeCallback: (i, _, value) =>
                {
                    if(value)
                    {
                        this.Log(LogLevel.Noisy, "PPI disable channel {0}", i);
                        channelEnabled[i].Value = false;
                    }
                }, valueProviderCallback: (i, _) => channelEnabled[i].Value)
            ;

            for(var i = 0; i < ConfigurableChannels; i++)
            {
                var j = i;

                ((Registers)((int)Registers.Channel0EventEndpoint + i * 8)).Define(this, name: "CH[n].EEP")
                    .WithValueField(0, 32, changeCallback: (oldValue, newValue) => UpdateEventEndpoint((uint)oldValue, (uint)newValue, j))
                ;

                ((Registers)((int)Registers.Channel0TaskEndpoint + i * 8)).Define(this, name: "CH[n].TEP")
                    .WithValueField(0, 32, changeCallback: (_, value) =>
                    {
                        taskEndpoint[j] = (uint)value;
                        this.Log(LogLevel.Noisy, "Connected channel {0} to trigger a task at 0x{1:X}", j, taskEndpoint[j]);
                    })
                ;
            }

            for(var i = 0; i < ChannelGroups; i++)
            {
                var j = i;
                ((Registers)((int)Registers.ChannelGroup0 + i * 4)).Define(this, name: "CHG[n]")
                    .WithFlags(0, 32, out channelGroups[j])
                ;
            }

            for(var i = 0; i < Channels; i++)
            {
                var j = i;

                ((Registers)((int)Registers.Fork0TaskEndpoint + i * 4)).Define(this, name: "FORK[n].TEP")
                    .WithValueField(0, 32, changeCallback: (_, value) =>
                    {
                        forkEndpoint[j] = (uint)value;
                        this.Log(LogLevel.Noisy, "Connected channel {0} to trigger a fork task at 0x{1:X}", j, taskEndpoint[j]);
                    })
                ;
            }
        }

        private void DefinePreprogrammedChannels()
        {
            // Predefined channels:
            var entries = new List<Tuple<uint, uint>>
            {
                // Timer0 Compare0 -> Radio TxEn
                Tuple.Create(0x40008140u, 0x40001000u),
                // Timer0 Compare0 -> Radio RxEn
                Tuple.Create(0x40008140u, 0x40001004u),
                // Timer0 Compare1 -> Radio Disable
                Tuple.Create(0x40008144u, 0x40001010u),
                // Radio BCMatch -> AAR Start
                Tuple.Create(0x40001128u, 0x4000F000u),
                // Radio Ready -> CCM KSGen
                Tuple.Create(0x40001100u, 0x4000F000u), //this address points both to AAR Start and CCM KSGen
                // Radio Address -> CCM Crypt
                Tuple.Create(0x40001104u, 0x4000F004u),
                // Radio Address -> Timer0 Capture1
                Tuple.Create(0x40001104u, 0x40008044u),
                // Radio End -> Timer0 Capture2
                Tuple.Create(0x4000110Cu, 0x40008048u),
                // RTC0 Compare0 -> Radio TxEn
                Tuple.Create(0x4000B140u, 0x40001000u),
                // RTC0 Compare0 -> Radio RxEn
                Tuple.Create(0x4000B140u, 0x40001004u),
                // RTC0 Compare0 -> Timer0 Clear
                Tuple.Create(0x4000B140u, 0x4000800Cu),
                // RTC0 Compare0 -> Timer0 Start
                Tuple.Create(0x4000B140u, 0x40008000u),
            };
            for(var i = ConfigurableChannels; i < Channels; i++)
            {
                var entry = entries[i - ConfigurableChannels];
                UpdateEventEndpoint(0, entry.Item1, i);
                taskEndpoint[i] = entry.Item2;
            }
        }

        private void UpdateEventEndpoint(uint oldValue, uint newValue, int eventId)
        {
            var oldProvider = eventProviders[eventId];
            if(oldProvider != null)
            {
                eventProviders[eventId] = null;
                if(Array.IndexOf(eventProviders, oldProvider) == -1)
                {
                    oldProvider.EventTriggered -= eventCallbacks[oldProvider];
                    eventCallbacks.Remove(oldProvider);
                }
                this.Log(LogLevel.Debug, "Disconnected channel {1} from event 0x{0:X}", oldValue, eventId);
            }
            eventEndpoint[eventId] = newValue;
            if(newValue != 0)
            {
                var target = sysbus.WhatPeripheralIsAt(newValue);
                if(target is INRFEventProvider nrfTarget)
                {
                    if(!eventCallbacks.ContainsKey(nrfTarget))
                    {
                        eventCallbacks[nrfTarget] = offset => EventReceived(nrfTarget, offset);
                        nrfTarget.EventTriggered += eventCallbacks[nrfTarget];
                    }
                    eventProviders[eventId] = nrfTarget;
                    this.Log(LogLevel.Debug, "Connected channel {1} to event 0x{0:X}", newValue, eventId);
                }
                else
                {
                    this.Log(LogLevel.Error, "Failed to register PPI from 0x{0:X} for channel {1}", newValue, eventId);
                }
            }
        }

        // Tasks run at once, as on hardware. Deferred to the next synced state, a task whose event came before a CPU
        // write in the same quantum would run after that write.
        private void EventReceived(INRFEventProvider provider, uint offset)
        {
            raisedEvents.Add((provider, offset));
            if(dispatchingEvents)
            {
                // A task of the round under way raised it (on hardware it comes a clock later, after all of that
                // round's tasks), or RaiseTogether's caller did.
                return;
            }
            dispatchingEvents = true;
            try
            {
                DispatchRaisedEvents();
            }
            finally
            {
                raisedEvents.Clear();
                dispatchingEvents = false;
            }
        }

        // Each round triggers the channels on the events raised in the round before. A cascade without a loop passes
        // each channel at most once, so it ends within Channels rounds; a longer one is channels triggering each other
        // in a loop, which on hardware would take time.
        private void DispatchRaisedEvents()
        {
            for(var round = 0; raisedEvents.Count > 0; round++)
            {
                var events = raisedEvents;
                raisedEvents = new List<(INRFEventProvider, uint)>();
                var triggeredChannels = new List<int>();
                var tasks = CollectTasks(events, triggeredChannels);
                if(round == Channels && tasks.Count > 0)
                {
                    throw new RecoverableException($"PPI channels {string.Join(", ", triggeredChannels)} keep triggering each other at one instant: a loop that would take time on hardware");
                }
                foreach(var task in tasks)
                {
                    sysbus.WriteDoubleWord(task, 1);
                }
            }
        }

        // Hardware triggers all channels on simultaneous events together, so which of them fire is decided before any
        // task runs, and a task triggered more than once runs once.
        private List<uint> CollectTasks(List<(INRFEventProvider, uint)> events, List<int> triggeredChannels)
        {
            var tasks = new List<uint>();
            for(var id = 0; id < Channels; id++)
            {
                if(!IsRaised(events, id) || !IsChannelEnabled(id))
                {
                    continue;
                }
                triggeredChannels.Add(id);
                if(taskEndpoint[id] != 0)
                {
                    this.Log(LogLevel.Noisy, "Received an event on channel {0} from 0x{1:X}. Triggering task at 0x{2:X}", id, eventEndpoint[id], taskEndpoint[id]);
                    if(!tasks.Contains(taskEndpoint[id]))
                    {
                        tasks.Add(taskEndpoint[id]);
                    }
                }
                else
                {
                    this.Log(LogLevel.Warning, "Received an event on channel {0} from 0x{1:X}, but there is no task configured", id, eventEndpoint[id]);
                }
                if(forkEndpoint[id] != 0)
                {
                    this.Log(LogLevel.Noisy, "Received an event on channel {0} from 0x{1:X}. Triggering fork task at 0x{2:X}", id, eventEndpoint[id], forkEndpoint[id]);
                    if(!tasks.Contains(forkEndpoint[id]))
                    {
                        tasks.Add(forkEndpoint[id]);
                    }
                }
            }
            if(tasks.Count > 1)
            {
                // Of simultaneous tasks the specification ranks, only the winner runs: a channel group's EN over its
                // DIS, a TIMER's STOP over its START.
                var losers = tasks.Where(task => TryGetWinningTask(task, out var winner) && tasks.Contains(winner)).ToList();
                tasks.RemoveAll(losers.Contains);
            }
            return tasks;
        }

        private bool IsRaised(List<(INRFEventProvider Provider, uint Offset)> events, int id)
        {
            foreach(var raised in events)
            {
                if(raised.Provider == eventProviders[id] && raised.Offset == (eventEndpoint[id] & RegisterOffsetMask))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsChannelEnabled(int id)
        {
            if(channelEnabled[id].Value)
            {
                return true;
            }
            // if the channel is disabled, it may be enabled by a group
            for(var i = 0; i < ChannelGroups; i++)
            {
                if(channelGroupEnabled[i] && channelGroups[i][id].Value)
                {
                    return true;
                }
            }
            this.Log(LogLevel.Noisy, "Received an event on channel {0} from 0x{1:X}, but it's disabled.", id, eventEndpoint[id]);
            return false;
        }

        private bool TryGetWinningTask(uint task, out uint winner)
        {
            var offset = task & RegisterOffsetMask;
            var target = sysbus.WhatPeripheralIsAt(task);
            if(target == this && offset <= (uint)Registers.ChannelGroup5Disable && offset % 8 == 4)
            {
                // TASKS_CHG[n].DIS, whose TASKS_CHG[n].EN is just before it
                winner = task - 4;
                return true;
            }
            if(target is NRF52840_Timer && offset == TimerStartTask)
            {
                winner = task - TimerStartTask + TimerStopTask;
                return true;
            }
            winner = 0;
            return false;
        }

        private IFlagRegisterField[] channelEnabled = new IFlagRegisterField[Channels];

        private bool initialized; // should not be reset - used for delayed configuration after the machine is created
        private bool dispatchingEvents;
        // events raised for the next round of EventReceived
        private List<(INRFEventProvider, uint)> raisedEvents = new List<(INRFEventProvider, uint)>();
        private readonly uint[] eventEndpoint = new uint[Channels];
        private readonly uint[] taskEndpoint = new uint[Channels];
        private readonly uint[] forkEndpoint = new uint[Channels];
        private readonly bool[] channelGroupEnabled = new bool[ChannelGroups];

        private readonly IFlagRegisterField[][] channelGroups = new IFlagRegisterField[ChannelGroups][];

        private readonly INRFEventProvider[] eventProviders = new INRFEventProvider[Channels];

        private readonly Dictionary<INRFEventProvider, Action<uint>> eventCallbacks = new Dictionary<INRFEventProvider, Action<uint>>();

        private const int RegisterOffsetMask = 0xFFF;
        private const int ChannelGroups = 6;
        private const int ConfigurableChannels = 20;
        private const int Channels = 32;
        private const uint TimerStartTask = 0x000;
        private const uint TimerStopTask = 0x004;

        private enum Registers
        {
            ChannelGroup0Enable = 0x000,
            ChannelGroup0Disable = 0x004,
            ChannelGroup1Enable = 0x008,
            ChannelGroup1Disable = 0x00C,
            ChannelGroup2Enable = 0x010,
            ChannelGroup2Disable = 0x014,
            ChannelGroup3Enable = 0x018,
            ChannelGroup3Disable = 0x01C,
            ChannelGroup4Enable = 0x020,
            ChannelGroup4Disable = 0x024,
            ChannelGroup5Enable = 0x028,
            ChannelGroup5Disable = 0x02C,
            ChannelEnable = 0x500,
            ChannelEnableSet = 0x504,
            ChannelEnableClear = 0x508,
            Channel0EventEndpoint = 0x510,
            Channel0TaskEndpoint = 0x514,
            Channel1EventEndpoint = 0x518,
            Channel1TaskEndpoint = 0x51C,
            Channel2EventEndpoint = 0x520,
            Channel2TaskEndpoint = 0x524,
            Channel3EventEndpoint = 0x528,
            Channel3TaskEndpoint = 0x52C,
            Channel4EventEndpoint = 0x530,
            Channel4TaskEndpoint = 0x534,
            Channel5EventEndpoint = 0x538,
            Channel5TaskEndpoint = 0x53C,
            Channel6EventEndpoint = 0x540,
            Channel6TaskEndpoint = 0x544,
            Channel7EventEndpoint = 0x548,
            Channel7TaskEndpoint = 0x54C,
            Channel8EventEndpoint = 0x550,
            Channel8TaskEndpoint = 0x554,
            Channel9EventEndpoint = 0x558,
            Channel9TaskEndpoint = 0x55C,
            Channel10EventEndpoint = 0x560,
            Channel10TaskEndpoint = 0x564,
            Channel11EventEndpoint = 0x568,
            Channel11TaskEndpoint = 0x56C,
            Channel12EventEndpoint = 0x570,
            Channel12TaskEndpoint = 0x574,
            Channel13EventEndpoint = 0x578,
            Channel13TaskEndpoint = 0x57C,
            Channel14EventEndpoint = 0x580,
            Channel14TaskEndpoint = 0x584,
            Channel15EventEndpoint = 0x588,
            Channel15TaskEndpoint = 0x58C,
            Channel16EventEndpoint = 0x590,
            Channel16TaskEndpoint = 0x594,
            Channel17EventEndpoint = 0x598,
            Channel17TaskEndpoint = 0x59c,
            Channel18EventEndpoint = 0x5A0,
            Channel18TaskEndpoint = 0x5A4,
            Channel19EventEndpoint = 0x5A8,
            Channel19TaskEndpoint = 0x5AC,
            ChannelGroup0 = 0x800,
            ChannelGroup1 = 0x804,
            ChannelGroup2 = 0x808,
            ChannelGroup3 = 0x80C,
            ChannelGroup4 = 0x810,
            ChannelGroup5 = 0x814,
            Fork0TaskEndpoint = 0x910,
            Fork1TaskEndpoint = 0x914,
            Fork2TaskEndpoint = 0x918,
            Fork3TaskEndpoint = 0x91C,
            Fork4TaskEndpoint = 0x920,
            Fork5TaskEndpoint = 0x924,
            Fork6TaskEndpoint = 0x928,
            Fork7TaskEndpoint = 0x92C,
            Fork8TaskEndpoint = 0x930,
            Fork9TaskEndpoint = 0x934,
            Fork10TaskEndpoint = 0x938,
            Fork11TaskEndpoint = 0x93C,
            Fork12TaskEndpoint = 0x940,
            Fork13TaskEndpoint = 0x944,
            Fork14TaskEndpoint = 0x948,
            Fork15TaskEndpoint = 0x94C,
            Fork16TaskEndpoint = 0x950,
            Fork17TaskEndpoint = 0x954,
            Fork18TaskEndpoint = 0x958,
            Fork19TaskEndpoint = 0x95C,
            Fork20TaskEndpoint = 0x960,
            Fork21TaskEndpoint = 0x964,
            Fork22TaskEndpoint = 0x968,
            Fork23TaskEndpoint = 0x96C,
            Fork24TaskEndpoint = 0x970,
            Fork25TaskEndpoint = 0x974,
            Fork26TaskEndpoint = 0x978,
            Fork27TaskEndpoint = 0x97C,
            Fork28TaskEndpoint = 0x980,
            Fork29TaskEndpoint = 0x984,
            Fork30TaskEndpoint = 0x988,
            Fork31TaskEndpoint = 0x98C,
        }
    }
}