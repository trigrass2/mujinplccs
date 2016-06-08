﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace mujinplccs
{
    public sealed class PLCController
    {
        private PLCMemory memory = null;
        private string heartbeatSignal = "";
        private DateTime lastHeartbeatTimestamp = DateTime.MinValue;
        private TimeSpan? maxHeartbeatInterval = null;
        private Dictionary<string, object> state = new Dictionary<string, object>();
        private BlockingCollection<IDictionary<string, object>> queue = new BlockingCollection<IDictionary<string, object>>(
            new ConcurrentQueue<IDictionary<string, object>>()
        );

        /// <summary>
        /// 
        /// </summary>
        /// <param name="memory"></param>
        /// <param name="maxHeartbeatInterval">Max time allowed before declaring disconnection when heartbeat signal is not received.</param>
        /// <param name="heartbeatSignal">Name of the heartbeat signal, default to empty which means any memory modification by anyone is considered as heartbeat.</param>
        public PLCController(PLCMemory memory, TimeSpan? maxHeartbeatInterval = null, string heartbeatSignal = "")
        {
            this.memory = memory;
            this.maxHeartbeatInterval = maxHeartbeatInterval;
            this.heartbeatSignal = heartbeatSignal;

            // register observer
            this.memory.Modified += new PLCMemory.Observer(this._Enqueue);

            // copy memory
            lock (memory)
            {
                foreach (var pair in memory)
                {
                    this.state[pair.Key] = pair.Value;
                }
            }
        }

        public bool IsConnected
        {
            get
            {
                if (this.maxHeartbeatInterval.HasValue)
                {
                    return DateTime.Now - this.lastHeartbeatTimestamp < this.maxHeartbeatInterval.Value;
                }
                return true;
            }
        }

        private void _Enqueue(IDictionary<string, object> modifications)
        {
            if (this.heartbeatSignal == "" || modifications.ContainsKey(this.heartbeatSignal))
            {
                lock (this)
                {
                    // timestamp of last activity
                    this.lastHeartbeatTimestamp = DateTime.Now;
                }
            }
            queue.Add(modifications);
        }

        private void _DequeueAll()
        {
            IDictionary<string, object> modifications;
            while (queue.TryTake(out modifications))
            {
                foreach (var pair in modifications)
                {
                    this.state[pair.Key] = pair.Value;
                }
            }
        }

        private IDictionary<string, object> _Dequeue(TimeSpan? timeout = null, bool timeoutOnDisconnect = true)
        {
            var start = DateTime.Now;
            IDictionary<string, object> modifications;
            while (true)
            {
                if (queue.TryTake(out modifications, TimeSpan.FromMilliseconds(50)))
                {
                    // successfully took
                    break;
                }

                // see if we timed out
                if (timeout.HasValue && (DateTime.Now - start) > timeout.Value)
                {
                    throw new TimeoutException();
                }

                // if disconnection is detected, immediately timeout.
                if (timeoutOnDisconnect && !this.IsConnected)
                {
                    throw new TimeoutException();
                }
            }

            // apply the modification to local state
            foreach (var pair in modifications)
            {
                this.state[pair.Key] = pair.Value;
            }
            return modifications;
        }

        public void Sync()
        {
            this._DequeueAll();
        }

        /// <summary>
        /// Wait until IsConnected becomes true.
        /// </summary>
        /// <param name="timeout"></param>
        public void WaitTillConnected(TimeSpan? timeout = null)
        {
            while (!this.IsConnected)
            {
                var start = DateTime.Now;
                this._Dequeue(timeout, false);
                timeout = timeout?.Subtract(DateTime.Now - start);
            }
        }

        /// <summary>
        /// Wait for a key to change to a particular value. If the key is already at such value, wait until it changes and changes back.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public object WaitFor(string key, object value, TimeSpan? timeout = null)
        {
            return this.WaitFor(new Dictionary<string, object>()
            {
                { key, value },
            }, timeout).Value;
        }

        public KeyValuePair<string, object> WaitFor(IDictionary<string, object> signals, TimeSpan? timeout = null)
        {
            while (true)
            {
                var start = DateTime.Now;
                var modifications = this._Dequeue(timeout);
                foreach (var pair in modifications)
                {
                    if (signals.ContainsKey(pair.Key))
                    {
                        if (signals[pair.Key] == null || pair.Value.Equals(signals[pair.Key]))
                        {
                            return pair;
                        }
                    }
                }
                timeout = timeout?.Subtract(DateTime.Now - start);
            }
        }

        public void WaitUntil(string key, object value, TimeSpan? timeout = null)
        {
            this.WaitUntil(new Dictionary<string, object>()
            {
                { key, value },
            }, null, timeout);
        }

        public void WaitUntil(IDictionary<string, object> expectations, IDictionary<string, object> exceptions = null, TimeSpan? timeout = null)
        {
            // combine all signals
            var all = new Dictionary<string, object>(expectations);
            if (exceptions != null)
            {
                foreach (var pair in exceptions)
                {
                    all[pair.Key] = pair.Value;
                }
            }

            // always clear the queue first
            this._DequeueAll();

            while (true)
            {
                // check if any exceptions is already met
                if (exceptions != null)
                {
                    foreach (var pair in exceptions)
                    {
                        if (this.state.ContainsKey(pair.Key) && this.state[pair.Key].Equals(pair.Value))
                        {
                            return;
                        }
                    }

                }
                                
                // check if all expectations are already met
                bool met = true;
                foreach (var pair in expectations)
                {
                    if (!this.state.ContainsKey(pair.Key) || !this.state[pair.Key].Equals(pair.Value))
                    {
                        met = false;
                        break;
                    }
                }
                if (met)
                {
                    return;
                }

                // wait for it to change
                var start = DateTime.Now;
                this.WaitFor(all, timeout);
                timeout = timeout?.Subtract(DateTime.Now - start);
            }
        }

        /// <summary>
        /// Get value of a key in the current state snapshot of the PLC memory.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="defaultValue"></param>
        /// <returns></returns>
        public object Get(string key, object defaultValue = null)
        {
            if (this.state.ContainsKey(key))
            {
                return this.state[key];
            }
            return defaultValue;
        }

        /// <summary>
        /// Get multiple keys in the current state snapshot of the PLC memory.
        /// </summary>
        /// <param name="keys"></param>
        /// <returns></returns>
        public IDictionary<string, object> Get(string[] keys)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            foreach (var key in keys)
            {
                if (this.state.ContainsKey(key))
                {
                    values[key] = this.state[key];
                }
            }
            return values;
        }

        /// <summary>
        /// Set key in PLC memory.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void Set(string key, object value)
        {
            this.memory.Write(new Dictionary<string, object>()
            {
                { key, value },
            });
        }

        /// <summary>
        /// Set multiple keys in PLC memory.
        /// </summary>
        /// <param name="values"></param>
        public void Set(IDictionary<string, object> values)
        {
            this.memory.Write(values);
        }
    }
}
