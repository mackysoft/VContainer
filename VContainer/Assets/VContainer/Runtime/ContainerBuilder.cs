using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using VContainer.Diagnostics;
using VContainer.Internal;
#if VCONTAINER_PARALLEL_CONTAINER_BUILD
using System.Threading.Tasks;
#endif

namespace VContainer
{
    public interface IContainerBuilder
    {
        object ApplicationOrigin { get; set; }
        DiagnosticsCollector Diagnostics { get; set; }

        T Register<T>(T registrationBuilder) where T : RegistrationBuilder;
        void RegisterBuildCallback(Action<IObjectResolver> container);
        bool Exists(Type type, bool includeInterfaceTypes = false);
    }

    public sealed class ScopedContainerBuilder : ContainerBuilder
    {
        readonly IObjectResolver root;
        readonly IScopedObjectResolver parent;

        internal ScopedContainerBuilder(IObjectResolver root, IScopedObjectResolver parent)
        {
            this.root = root;
            this.parent = parent;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IScopedObjectResolver BuildScope()
        {
            var registry = BuildRegistry();
            var container = new ScopedContainer(registry, root, parent);
            container.Diagnostics = Diagnostics;
            EmitCallbacks(container);
            return container;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override IObjectResolver Build() => BuildScope();
    }

    public class ContainerBuilder : IContainerBuilder
    {
        public object ApplicationOrigin { get; set; }

        public DiagnosticsCollector Diagnostics
        {
            get => diagnostics;
            set
            {
                diagnostics = value;
                diagnostics?.Clear();
            }
        }

        readonly List<RegistrationBuilder> registrationBuilders = new List<RegistrationBuilder>();
        List<Action<IObjectResolver>> buildCallbacks;
        DiagnosticsCollector diagnostics;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Register<T>(T registrationBuilder) where T : RegistrationBuilder
        {
            registrationBuilders.Add(registrationBuilder);
            Diagnostics?.TraceRegister(new RegisterInfo(registrationBuilder));
            return registrationBuilder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RegisterBuildCallback(Action<IObjectResolver> callback)
        {
            if (buildCallbacks == null)
                buildCallbacks = new List<Action<IObjectResolver>>();
            buildCallbacks.Add(callback);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(Type type, bool includeInterfaceTypes = false)
        {
            foreach (var registrationBuilder in registrationBuilders)
            {
                if (registrationBuilder.ImplementationType == type ||
                    includeInterfaceTypes && registrationBuilder.InterfaceTypes?.Contains(type) == true)
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public virtual IObjectResolver Build()
        {
            var registry = BuildRegistry();
            var container = new Container(registry);
            container.Diagnostics = Diagnostics;
            EmitCallbacks(container);
            return container;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected IRegistry BuildRegistry()
        {
            var registrations = new IRegistration[registrationBuilders.Count + 1];

#if VCONTAINER_PARALLEL_CONTAINER_BUILD
            Parallel.For(0, registrationBuilders.Count, i =>
            {
                var registrationBuilder = registrationBuilders[i];
                var registration = registrationBuilder.Build();
                Diagnostics?.TraceBuild(this, registrationBuilder, registration);
                registrations[i] = registration;
            });
#else
            for (var i = 0; i < registrationBuilders.Count; i++)
            {
                var registrationBuilder = registrationBuilders[i];
                var registration = registrationBuilder.Build();
                Diagnostics?.TraceBuild(registrationBuilder, registration);
                registrations[i] = registration;
            }
#endif
            registrations[registrations.Length - 1] = ContainerRegistration.Default;

            var registry = FixedTypeKeyHashTableRegistry.Build(registrations);
            TypeAnalyzer.CheckCircularDependency(registrations, registry);

            return registry;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void EmitCallbacks(IObjectResolver container)
        {
            if (buildCallbacks == null) return;

            foreach (var callback in buildCallbacks)
            {
                callback.Invoke(container);
            }

            Diagnostics?.NotifyContainerBuilt(container);
        }
    }
}
