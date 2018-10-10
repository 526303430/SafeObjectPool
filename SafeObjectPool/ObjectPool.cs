﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SafeObjectPool {

	/// <summary>
	/// 对象池管理类
	/// </summary>
	/// <typeparam name="T">对象类型</typeparam>
	public partial class ObjectPool<T> {

		public IPolicy<T> Policy { get; protected set; }

		private List<Object<T>> _allObjects = new List<Object<T>>();
		private object _allObjectsLock = new object();
		private ConcurrentQueue<Object<T>> _freeObjects = new ConcurrentQueue<Object<T>>();

		private ConcurrentQueue<GetSyncQueueInfo> _getSyncQueue = new ConcurrentQueue<GetSyncQueueInfo>();
		private ConcurrentQueue<TaskCompletionSource<Object<T>>> _getAsyncQueue = new ConcurrentQueue<TaskCompletionSource<Object<T>>>();
		private ConcurrentQueue<bool> _getQueue = new ConcurrentQueue<bool>();

		/// <summary>
		/// 是否可用
		/// </summary>
		public bool IsAvailable { get; private set; } = true;
		/// <summary>
		/// 不可用时间
		/// </summary>
		public DateTime? UnavailableTime { get; private set; }
		private object IsAvailableLock = new object();
		private static bool running = true;

		/// <summary>
		/// 将连接池设置为不可用，后续 Get/GetAsync 均会报错，同时启动后台定时检查服务恢复可用
		/// </summary>
		/// <returns>由【可用】变成【不可用】时返回true，否则返回false</returns>
		public bool SetUnavailable() {

			bool isseted = false;

			if (IsAvailable == true) {

				lock(IsAvailableLock) {

					if (IsAvailable == true) {

						IsAvailable = false;
						UnavailableTime = DateTime.Now;
						isseted = true;
					}
				}
			}

			if (isseted) {

				Policy.OnUnavailable();
				CheckAvailable(Policy.CheckAvailableInterval);
			}

			return isseted;
		}

		/// <summary>
		/// 后台定时检查可用性
		/// </summary>
		/// <param name="interval"></param>
		private void CheckAvailable(int interval) {

			new Thread(() => {

				if (IsAvailable == false) Console.WriteLine($"【{Policy.Name}】恢复检查时间：{DateTime.Now.AddSeconds(interval)}");

				while (IsAvailable == false) {

					if (running == false) return;

					Thread.CurrentThread.Join(TimeSpan.FromSeconds(interval));

					if (running == false) return;

					try {

						var conn = getFree(false);
						if (conn == null) throw new Exception($"CheckAvailable 无法获得资源，{this.Statistics}");

						try {

							if (Policy.OnCheckAvailable(conn.Value) == false) throw new Exception("CheckAvailable 应抛出异常，代表仍然不可用。");
							break;

						} finally {

							Return(conn);
						}

					} catch (Exception ex) {
						Console.WriteLine($"【{Policy.Name}】仍然不可用，下一次恢复检查时间：{DateTime.Now.AddSeconds(interval)}，错误：({ex.Message})");
					}
				}

				bool isRestored = false;
				if (IsAvailable == false) {

					lock (IsAvailableLock) {

						if (IsAvailable == false) {

							IsAvailable = true;
							UnavailableTime = null;
							isRestored = true;
						}
					}
				}

				if (isRestored) {

					lock (_allObjectsLock)
						_allObjects.ForEach(a => a.LastGetTime = a.LastReturnTime = new DateTime(2000, 1, 1));

					Policy.OnAvailable();
					Console.WriteLine($"【{Policy.Name}】已恢复工作");
				}

			}).Start();
		}

		/// <summary>
		/// 统计
		/// </summary>
		public string Statistics => $"Pool: {_freeObjects.Count}/{_allObjects.Count}, Get wait: {_getSyncQueue.Count}, GetAsync wait: {_getAsyncQueue.Count}";
		/// <summary>
		/// 统计（完整)
		/// </summary>
		public string StatisticsFullily {
			get {
				var sb = new StringBuilder();

				sb.AppendLine(Statistics);
				sb.AppendLine("");

				foreach (var obj in _allObjects) {
					sb.AppendLine($"{obj.Value}, Times: {obj.GetTimes}, ThreadId(R/G): {obj.LastReturnThreadId}/{obj.LastGetThreadId}, Time(R/G): {obj.LastReturnTime.ToString("yyyy-MM-dd HH:mm:ss:ms")}/{obj.LastGetTime.ToString("yyyy-MM-dd HH:mm:ss:ms")}, ");
				}

				return sb.ToString();
			}
		}

		/// <summary>
		/// 创建对象池
		/// </summary>
		/// <param name="poolsize">池大小</param>
		/// <param name="createObject">池内对象的创建委托</param>
		/// <param name="onGetObject">获取池内对象成功后，进行使用前操作</param>
		public ObjectPool(int poolsize, Func<T> createObject, Action<Object<T>> onGetObject = null) : this(new DefaultPolicy<T> { PoolSize = poolsize, CreateObject = createObject, OnGetObject = onGetObject }) {
		}
		/// <summary>
		/// 创建对象池
		/// </summary>
		/// <param name="policy">策略</param>
		public ObjectPool(IPolicy<T> policy) {
			Policy = policy;

			AppDomain.CurrentDomain.ProcessExit += (s1, e1) => {
				running = false;
			};
			Console.CancelKeyPress += (s1, e1) => {
				running = false;
			};
		}

		/// <summary>
		/// 获取可用资源，或创建资源
		/// </summary>
		/// <returns></returns>
		private Object<T> getFree(bool checkAvailable) {

			if (checkAvailable && IsAvailable == false)
				throw new Exception($"【{Policy.Name}】状态不可用，等待后台检查程序恢复方可使用。");

			if ((_freeObjects.TryDequeue(out var obj) == false || obj == null) && _allObjects.Count < Policy.PoolSize) {

				lock (_allObjectsLock)
					if (_allObjects.Count < Policy.PoolSize)
						_allObjects.Add(obj = new Object<T> { Pool = this });

				if (obj != null)
					obj.Value = Policy.OnCreate();
			}

			return obj;
		}

		/// <summary>
		/// 获取资源
		/// </summary>
		/// <param name="timeout">超时</param>
		/// <returns>资源</returns>
		public Object<T> Get(TimeSpan? timeout = null) {

			var obj = getFree(true);

			if (obj == null) {

				var queueItem = new GetSyncQueueInfo();

				_getSyncQueue.Enqueue(queueItem);
				_getQueue.Enqueue(false);

				if (timeout == null) timeout = Policy.SyncGetTimeout;

				if (queueItem.Wait.Wait(timeout.Value))
					obj = queueItem.ReturnValue;

				if (obj == null) obj = queueItem.ReturnValue;
				if (obj == null) lock (queueItem.Lock) queueItem.IsTimeout = (obj = queueItem.ReturnValue) == null;
				if (obj == null) obj = queueItem.ReturnValue;

				if (obj == null) {

					Policy.OnGetTimeout();

					if (Policy.IsThrowGetTimeoutException)
						throw new Exception($"SafeObjectPool.Get 获取超时（{timeout.Value.TotalSeconds}秒），设置 Policy.IsThrowGetTimeoutException 可以避免该异常。");

					return null;
				}
			}

			try {
				Policy.OnGet(obj);
			} catch {
				Return(obj);
				throw;
			}

			obj.LastGetThreadId = Thread.CurrentThread.ManagedThreadId;
			obj.LastGetTime = DateTime.Now;
			Interlocked.Increment(ref obj._getTimes);

			return obj;
		}

		/// <summary>
		/// 获取资源
		/// </summary>
		/// <returns>资源</returns>
		async public Task<Object<T>> GetAsync() {

			var obj = getFree(true);

			if (obj == null) {

				if (Policy.AsyncGetCapacity > 0 && _getAsyncQueue.Count >= Policy.AsyncGetCapacity - 1)
					throw new Exception($"SafeObjectPool.GetAsync 无可用资源且队列过长，Policy.AsyncGetCapacity = {Policy.AsyncGetCapacity}。");

				var tcs = new TaskCompletionSource<Object<T>>();

				_getAsyncQueue.Enqueue(tcs);
				_getQueue.Enqueue(true);

				obj = await tcs.Task;

				//if (timeout == null) timeout = Policy.SyncGetTimeout;

				//if (tcs.Task.Wait(timeout.Value))
				//	obj = tcs.Task.Result;

				//if (obj == null) {

				//	tcs.TrySetCanceled();
				//	Policy.GetTimeout();

				//	if (Policy.IsThrowGetTimeoutException)
				//		throw new Exception($"SafeObjectPool.GetAsync 获取超时（{timeout.Value.TotalSeconds}秒），设置 Policy.IsThrowGetTimeoutException 可以避免该异常。");

				//	return null;
				//}
			}

			try {
				await Policy.OnGetAsync(obj);
			} catch {
				Return(obj);
				throw;
			}

			obj.LastGetThreadId = Thread.CurrentThread.ManagedThreadId;
			obj.LastGetTime = DateTime.Now;
			Interlocked.Increment(ref obj._getTimes);

			return obj;
		}

		/// <summary>
		/// 使用完毕后，归还资源
		/// </summary>
		/// <param name="obj">对象</param>
		/// <param name="isRecreate">是否重新创建</param>
		public void Return(Object<T> obj, bool isRecreate = false) {

			if (obj == null) return;

			if (isRecreate) {

				(obj.Value as IDisposable)?.Dispose();

				obj.Value = Policy.OnCreate();
			}

			bool isReturn = false;

			while (isReturn == false && _getQueue.TryDequeue(out var isAsync)) {

				if (isAsync == false) {

					if (_getSyncQueue.TryDequeue(out var queueItem) && queueItem != null) {

						lock (queueItem.Lock)
							if (queueItem.IsTimeout == false)
								queueItem.ReturnValue = obj;

						if (queueItem.ReturnValue != null) {

							obj.LastReturnThreadId = Thread.CurrentThread.ManagedThreadId;
							obj.LastReturnTime = DateTime.Now;

							queueItem.Wait.Set();
							isReturn = true;
						}

						queueItem.Dispose();
					}

				} else {

					if (_getAsyncQueue.TryDequeue(out var tcs) && tcs != null && tcs.Task.IsCanceled == false) {

						obj.LastReturnThreadId = Thread.CurrentThread.ManagedThreadId;
						obj.LastReturnTime = DateTime.Now;

						isReturn = tcs.TrySetResult(obj);
					}
				}
			}

			//无排队，直接归还
			if (isReturn == false) {

				try {

					Policy.OnReturn(obj);

				} catch {

					throw;

				} finally {

					obj.LastReturnThreadId = Thread.CurrentThread.ManagedThreadId;
					obj.LastReturnTime = DateTime.Now;

					_freeObjects.Enqueue(obj);
				}
			}
		}

		class GetSyncQueueInfo : IDisposable {

			internal ManualResetEventSlim Wait { get; set; } = new ManualResetEventSlim();

			internal Object<T> ReturnValue { get; set; }

			internal object Lock = new object();

			internal bool IsTimeout { get; set; } = false;

			public void Dispose() {
				try {
					if (Wait != null)
						Wait.Dispose();
				} catch {
				}
			}
		}
	}
}