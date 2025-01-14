﻿using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using VContainer.Diagnostics;
using VContainer.Internal;

namespace VContainer
{
    public interface IObjectResolver : IDisposable
    {
        DiagnosticsCollector Diagnostics { get; set; }

        object Resolve(Type type);
        object Resolve(IRegistration registration);
        IScopedObjectResolver CreateScope(Action<IContainerBuilder> installation = null);
        void Inject(object instance);
    }

    public interface IScopedObjectResolver : IObjectResolver
    {
        IObjectResolver Root { get; }
        IScopedObjectResolver Parent { get; }
        bool TryGetRegistration(Type type, out IRegistration registration);
    }

    public enum Lifetime
    {
        Transient,
        Singleton,
        Scoped
    }

    public sealed class ScopedContainer : IScopedObjectResolver
    {
        public IObjectResolver Root { get; }
        public IScopedObjectResolver Parent { get; }
        public DiagnosticsCollector Diagnostics { get; set; }

        readonly IRegistry registry;
        readonly ConcurrentDictionary<IRegistration, Lazy<object>> sharedInstances = new ConcurrentDictionary<IRegistration, Lazy<object>>();
        readonly CompositeDisposable disposables = new CompositeDisposable();
        readonly Func<IRegistration, Lazy<object>> createInstance;

        internal ScopedContainer(
            IRegistry registry,
            IObjectResolver root,
            IScopedObjectResolver parent = null)
        {
            Root = root;
            Parent = parent;
            this.registry = registry;

            createInstance = registration =>
            {
                return new Lazy<object>(() => registration.SpawnInstance(this));
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(Type type) => Resolve(FindRegistration(type));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(IRegistration registration)
        {
            if (Diagnostics != null)
            {
                return Diagnostics.TraceResolve(registration, ResolveCore);
            }
            return ResolveCore(registration);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IScopedObjectResolver CreateScope(Action<IContainerBuilder> installation = null)
        {
            var containerBuilder = new ScopedContainerBuilder(Root, this);
            installation?.Invoke(containerBuilder);
            return containerBuilder.BuildScope();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Inject(object instance)
        {
            var injector = InjectorCache.GetOrBuild(instance.GetType());
            injector.Inject(instance, this, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetRegistration(Type type, out IRegistration registration)
            => registry.TryGet(type, out registration);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            disposables.Dispose();
            sharedInstances.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object ResolveCore(IRegistration registration)
        {
            switch (registration.Lifetime)
            {
                case Lifetime.Singleton:
                    if (Parent is null)
                        return Root.Resolve(registration);

                    if (!registry.Exists(registration.ImplementationType))
                        return Parent.Resolve(registration);

                    return CreateTrackedInstance(registration);

                case Lifetime.Scoped:
                    return CreateTrackedInstance(registration);

                default:
                    return registration.SpawnInstance(this);
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object CreateTrackedInstance(IRegistration registration)
        {
            var lazy = sharedInstances.GetOrAdd(registration, createInstance);
            var created = lazy.IsValueCreated;
            var instance = lazy.Value;
            if (!created && instance is IDisposable disposable && !(registration is InstanceRegistration))
            {
                disposables.Add(disposable);
            }
            return instance;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IRegistration FindRegistration(Type type)
        {
            IScopedObjectResolver scope = this;
            while (scope != null)
            {
                if (scope.TryGetRegistration(type, out var registration))
                {
                    return registration;
                }
                scope = scope.Parent;
            }
            throw new VContainerException(type, $"No such registration of type: {type}");
        }
    }

    public sealed class Container : IObjectResolver
    {
        public DiagnosticsCollector Diagnostics { get; set; }

        readonly IRegistry registry;
        readonly IScopedObjectResolver rootScope;
        readonly ConcurrentDictionary<IRegistration, Lazy<object>> sharedInstances = new ConcurrentDictionary<IRegistration, Lazy<object>>();
        readonly CompositeDisposable disposables = new CompositeDisposable();
        readonly Func<IRegistration, Lazy<object>> createInstance;

        internal Container(IRegistry registry)
        {
            this.registry = registry;
            rootScope = new ScopedContainer(registry, this);

            createInstance = registration =>
            {
                return new Lazy<object>(() => registration.SpawnInstance(this));
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(Type type)
        {
            if (registry.TryGet(type, out var registration))
            {
                return Resolve(registration);
            }
            throw new VContainerException(type, $"No such registration of type: {type}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(IRegistration registration)
        {
            if (Diagnostics != null)
            {
                return Diagnostics.TraceResolve(registration, ResolveCore);
            }
            return ResolveCore(registration);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IScopedObjectResolver CreateScope(Action<IContainerBuilder> installation = null)
            => rootScope.CreateScope(installation);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Inject(object instance)
        {
            var injector = InjectorCache.GetOrBuild(instance.GetType());
            injector.Inject(instance, this, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            rootScope.Dispose();
            disposables.Dispose();
            sharedInstances.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object ResolveCore(IRegistration registration)
        {
            switch (registration.Lifetime)
            {
                case Lifetime.Singleton:
                    var singleton = sharedInstances.GetOrAdd(registration, createInstance);
                    if (!singleton.IsValueCreated && singleton.Value is IDisposable disposable && !(registration is InstanceRegistration))
                    {
                        disposables.Add(disposable);
                    }
                    return singleton.Value;

                case Lifetime.Scoped:
                    return rootScope.Resolve(registration);

                default:
                    return registration.SpawnInstance(this);
            }
        }
    }
}

