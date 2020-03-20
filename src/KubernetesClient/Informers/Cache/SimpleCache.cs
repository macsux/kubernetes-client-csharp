using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace k8s.Informers.Cache
{
    public class SimpleCache<TKey, TResource> : ICache<TKey, TResource>, ICacheSnapshot<TKey,TResource>
    {
        private readonly IDictionary<TKey, TResource> _items;

        public SimpleCache()
        {
            _items = new Dictionary<TKey, TResource>();
        }

        public SimpleCache(IDictionary<TKey, TResource> items, long version)
        {
            Version = version;
            _items = new Dictionary<TKey, TResource>(items);
        }

        public void Reset(IDictionary<TKey, TResource> newValues)
        {
            lock (SyncRoot)
            {
                _items.Clear();
                foreach (var item in newValues)
                {
                    _items.Add(item.Key, item.Value);
                }

            }
        }

        public object SyncRoot { get; } = new object();
        public ICacheSnapshot<TKey, TResource> Snapshot()
        {
            lock (SyncRoot)
            {
                return new SimpleCache<TKey, TResource>(this, Version);
            }
        }

        public IEnumerator<KeyValuePair<TKey, TResource>> GetEnumerator()
        {
            lock (SyncRoot)
            {
                return _items.ToList().GetEnumerator();
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            lock (SyncRoot)
            {
                return _items.ToList().GetEnumerator();
            }
        }

        public void Add(KeyValuePair<TKey, TResource> item)
        {
            lock (SyncRoot)
            {
                _items.Add(item);
            }
        }


        public void Clear()
        {
            lock (SyncRoot)
            {
                _items.Clear();
            }
        }

        public bool Contains(KeyValuePair<TKey, TResource> item)
        {
            lock (SyncRoot)
            {
                return _items.Contains(item);
            }
        }

        public void CopyTo(KeyValuePair<TKey, TResource>[] array, int arrayIndex)
        {
            lock (SyncRoot)
            {
                ((IDictionary<TKey, TResource>) _items).CopyTo(array, arrayIndex);
            }
        }

        public bool Remove(KeyValuePair<TKey, TResource> item)
        {
            lock (SyncRoot)
            {
                return _items.Remove(item.Key);
            }
        }

        public int Count
        {
            get
            {
                lock (SyncRoot)
                {
                    return _items.Count;
                }
            }
        }

        public bool IsReadOnly => false;

        public void Add(TKey key, TResource value)
        {
            lock (SyncRoot)
            {
                _items.Add(key, value);
            }
        }

        public bool ContainsKey(TKey key)
        {
            lock (SyncRoot)
            {
                return _items.ContainsKey(key);
            }
        }

        public bool Remove(TKey key)
        {
            lock (SyncRoot)
            {
                if (!_items.Remove(key, out var existing))
                    return false;
                return true;
            }
        }

        public bool TryGetValue(TKey key, out TResource value)
        {
            lock (SyncRoot)
            {
                return _items.TryGetValue(key, out value);
            }
        }

        public TResource this[TKey key]
        {
            get
            {
                lock (SyncRoot)
                {
                    return _items[key];
                }
            }
            set
            {
                lock (SyncRoot)
                {
                    _items[key] = value;
                }
            }
        }

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TResource>.Keys => Keys;

        IEnumerable<TResource> IReadOnlyDictionary<TKey, TResource>.Values => Values;

        public ICollection<TKey> Keys
        {
            get
            {
                lock (SyncRoot)
                {
                    return _items.Keys.ToList();
                }
            }
        }

        public ICollection<TResource> Values
        {
            get
            {
                lock (SyncRoot)
                {
                    return _items.Values.ToList();
                }
            }
        }

        public void Dispose()
        {
        }

        public long Version { get; set; } //= 1;
    }
}
