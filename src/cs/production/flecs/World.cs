using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using JetBrains.Annotations;
using static flecs_hub.flecs;

namespace flecs;

[PublicAPI]
public unsafe class World
{
    private readonly ecs_world_t* _worldHandle;
    private Dictionary<Type, ecs_entity_t> _componentHandlesByType = new();

    private readonly ecs_id_t ECS_PAIR;
    private readonly ecs_entity_t EcsOnUpdate;
    private readonly ecs_entity_t EcsDependsOn;

    public int ExitCode { get; private set; }

    public World(string[] args)
    {
        var argv = args.Length == 0 ? default : Runtime.CStrings.CStringArray(args);
        _worldHandle = ecs_init_w_args(args.Length, argv);
        Runtime.CStrings.FreeCStrings(argv, args.Length);

        ECS_PAIR = pinvoke_ECS_PAIR();
        EcsOnUpdate = pinvoke_EcsOnUpdate();
        EcsDependsOn = pinvoke_EcsDependsOn();
    }

    public int Fini()
    {
        var exitCode = ecs_fini(_worldHandle);
        return exitCode;
    }

    public ecs_entity_t InitializeComponent<TComponent>()
        where TComponent : unmanaged
    {
        var componentType = typeof(TComponent);
        var componentName = componentType.Name;
        var componentNameC = Runtime.CStrings.CString(componentName);
        var structLayoutAttribute = componentType.StructLayoutAttribute;
        CheckStructLayout(structLayoutAttribute);
        var structSize = Unsafe.SizeOf<TComponent>();
        var structAlignment = structLayoutAttribute!.Pack;

        var id = new ecs_entity_t();
        ecs_component_desc_t desc;
        desc.entity.entity = id;
        desc.entity.name = componentNameC;
        desc.entity.symbol = componentNameC;
        desc.type.size = structSize;
        desc.type.alignment = structAlignment;
        id = ecs_component_init(_worldHandle, &desc);
        Debug.Assert(id.Data.Data != 0, "ECS_INVALID_PARAMETER");
        return id;
    }
    
    public ecs_entity_t InitializeSystem(
        SystemCallback callback, ecs_entity_t phase, string filterExpression, string? name = null)
    {
        ecs_system_desc_t desc = default;
        FillSystemDescriptorCommon(ref desc, callback, phase, name);

        desc.query.filter.expr = filterExpression;

        var id = ecs_system_init(_worldHandle, &desc);
        return id;
    }

    public ecs_entity_t InitializeSystem<TComponent1>(
        SystemCallback callback, ecs_entity_t phase, string? name = null)
    {
        ecs_system_desc_t desc = default;
        FillSystemDescriptorCommon(ref desc, callback, phase, name);

        desc.query.filter.expr = GetComponentName<TComponent1>();

        var id = ecs_system_init(_worldHandle, &desc);
        return id;
    }

    public ecs_entity_t InitializeSystem<TComponent1, TComponent2>(
        SystemCallback callback, string? name = null)
    {
        var id = new ecs_entity_t();
        ecs_system_desc_t desc = default;
        desc.entity.name = name ?? callback.Method.Name;
        var phase = EcsOnUpdate;
        desc.entity.add[0] = phase.Data != 0 ? ecs_pair(EcsDependsOn, phase) : default;
        desc.entity.add[1] = phase;
        desc.callback.Data.Pointer = &SystemCallback;
        desc.binding_ctx = (void*)SystemBindingContextHelper.CreateSystemBindingContext(callback);

        var componentName1 = GetComponentName<TComponent1>();
        var componentName2 = GetComponentName<TComponent2>();
        desc.query.filter.expr = componentName1 + ", " + componentName2;

        id = ecs_system_init(_worldHandle, &desc);
        return id;
    }

    private void FillSystemDescriptorCommon(
        ref ecs_system_desc_t desc, SystemCallback callback, ecs_entity_t phase, string? name)
    {
        desc.entity.name = name ?? callback.Method.Name;
        desc.entity.add[0] = phase.Data != 0 ? ecs_pair(EcsDependsOn, phase) : default;
        desc.entity.add[1] = phase;
        desc.callback.Data.Pointer = &SystemCallback;
        desc.binding_ctx = (void*)SystemBindingContextHelper.CreateSystemBindingContext(callback);
    }

    public ecs_entity_t InitializeTag(string name)
    {
        ecs_entity_desc_t desc = default;
        desc.name = name;
        var id = ecs_entity_init(_worldHandle, &desc);
        Debug.Assert(id.Data != 0, "ECS_INVALID_PARAMETER");
        return id;
    }

    public ref TComponent GetComponent<TComponent>(ecs_entity_t entity, ecs_id_t id)
        where TComponent : unmanaged
    {
        var componentType = typeof(TComponent);
        var structLayoutAttribute = componentType.StructLayoutAttribute;
        CheckStructLayout(structLayoutAttribute);
        var pointer = ecs_get_id(_worldHandle, entity, id);
        return ref Unsafe.AsRef<TComponent>(pointer);
    }
    
    public ecs_entity_t SetComponent<TComponent>(ecs_entity_t entity, ecs_id_t componentId, ref TComponent component)
        where TComponent : unmanaged
    {
        var componentType = typeof(TComponent);
        var structLayoutAttribute = componentType.StructLayoutAttribute;
        CheckStructLayout(structLayoutAttribute);
        var structSize = Unsafe.SizeOf<TComponent>();
        var pointer = Unsafe.AsPointer(ref component);
        var result = ecs_set_id(_worldHandle, entity, componentId, (ulong)structSize, pointer);
        return result;
    }
    
    public ecs_entity_t SetComponent<TComponent>(ecs_entity_t entity, ecs_id_t componentId, TComponent component)
        where TComponent : unmanaged
    {
        var result = SetComponent(entity, componentId, ref component);
        return result;
    }

    [UnmanagedCallersOnly]
    private static void SystemCallback(ecs_iter_t* it)
    {
        SystemBindingContextHelper.GetSystemBindingContext((IntPtr)it->binding_ctx, out var data);
        
        var iterator = new Iterator(it);
        data.Callback(iterator);
    }

    public ecs_entity_t InitializeEntity(string name)
    {
        var desc = default(ecs_entity_desc_t);
        desc.name = name;
        var result = ecs_entity_init(_worldHandle, &desc);
        return result;
    }
    
    // #define ecs_pair(pred, obj) (ECS_PAIR | ecs_entity_t_comb(obj, pred))

    public void AddPair(ecs_entity_t subject, ecs_entity_t relation, ecs_entity_t @object)
    {
        var id = ecs_pair(relation, @object);
        ecs_add_id(_worldHandle, subject, id);
    }

    public bool Progress(float deltaTime)
    {
        return ecs_progress(_worldHandle, deltaTime);
    }

    private ulong ecs_pair(ecs_entity_t pred, ecs_entity_t obj)
    {
        return ECS_PAIR | ecs_entity_t_comb(obj.Data.Data, pred.Data.Data);
    }

    private ulong ecs_entity_t_comb(ulong lo, ulong hi)
    {
        return (hi << 32) + lo;
    }

    private static void CheckStructLayout(StructLayoutAttribute? structLayoutAttribute)
    {
        if (structLayoutAttribute == null || structLayoutAttribute.Value == LayoutKind.Auto)
        {
            throw new FlecsException(
                "Component must have a StructLayout attribute with LayoutKind sequential or explicit. This is to ensure that the struct fields are not reorganized by the C# compiler.");
        }
    }

    private string GetComponentName<TComponent>()
    {
        return typeof(TComponent).Name;
    }
}