using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor.Experimental.VFX;
using UnityEngine.Experimental.VFX;

using Type = System.Type;
using Object = UnityEngine.Object;

namespace UnityEditor.VFX
{
    [Flags]
    public enum VFXContextType
    {
        None = 0,

        Spawner = 1 << 0,
        Init = 1 << 1,
        Update = 1 << 2,
        Output = 1 << 3,
        Event = 1 << 4,
        SpawnerGPU = 1 << 5,
        Subgraph = 1 << 6,

        InitAndUpdate = Init | Update,
        InitAndUpdateAndOutput = Init | Update | Output,
        UpdateAndOutput = Update | Output,
        All = Init | Update | Output | Spawner | SpawnerGPU | Subgraph,
    };

    [Flags]
    public enum VFXDataType
    {
        None =         0,
        SpawnEvent =   1 << 0,
        Particle =     1 << 1,
        Mesh =         1 << 2,
    };

    [Serializable]
    struct VFXContextLink
    {
        public VFXContext context;
        public int slotIndex;
    }

    [Serializable]
    class VFXContextSlot
    {
        public List<VFXContextLink> link = new List<VFXContextLink>();
    }

    internal class VFXContext : VFXSlotContainerModel<VFXGraph, VFXBlock>
    {
        protected static string RenderPipeTemplate(string fileName)
        {
            return UnityEngine.Experimental.VFX.VFXManager.renderPipeSettingsPath + "/Templates/" + fileName;
        }

        [SerializeField]
        private string m_Label;

        public string label
        {
            get { return m_Label; }
            set {
                m_Label = value;
                Invalidate(InvalidationCause.kUIChanged);
            }
        }

        private VFXContext() { m_UICollapsed = false; } // Used by serialization

        public VFXContext(VFXContextType contextType, VFXDataType inputType, VFXDataType outputType)
        {
            m_ContextType = contextType;
            m_InputType = inputType;
            m_OutputType = outputType;
        }

        public VFXContext(VFXContextType contextType) : this(contextType, VFXDataType.None, VFXDataType.None)
        {}

        // Called by VFXData
        public static T CreateImplicitContext<T>(VFXData data) where T : VFXContext
        {
            var context = ScriptableObject.CreateInstance<T>();
            context.m_Data = data;
            return context;
        }

        public override void OnEnable()
        {
            base.OnEnable();

            // type must not be a combination of flags so test if it's a power of two
            if ((m_ContextType & (m_ContextType - 1)) != 0)
            {
                var invalidContext = m_ContextType;
                m_ContextType = VFXContextType.None;
                throw new ArgumentException(string.Format("Illegal context type: {0}", invalidContext));
            }

            int nbRemoved = 0;
            if (m_InputFlowSlot == null)
                m_InputFlowSlot = Enumerable.Range(0, inputFlowCount).Select(_ => new VFXContextSlot()).ToArray();
            else
                for (int i = 0; i < m_InputFlowSlot.Length; ++i)
                    nbRemoved += m_InputFlowSlot[i].link.RemoveAll(s => s.context == null);

            if (m_OutputFlowSlot == null)
                m_OutputFlowSlot = Enumerable.Range(0, outputFlowCount).Select(_ => new VFXContextSlot()).ToArray();
            else
                for (int i = 0; i < m_OutputFlowSlot.Length; ++i)
                    nbRemoved += m_OutputFlowSlot[i].link.RemoveAll(s => s.context == null);

            if (nbRemoved > 0)
                Debug.LogWarningFormat("Remove {0} linked context(s) that could not be deserialized from {1} of type {2}", nbRemoved, name, GetType());

            if (m_Data == null)
                SetDefaultData(false);

            m_UICollapsed = false;
        }

        public bool doesGenerateShader                                  { get { return codeGeneratorTemplate != null; } }
        public virtual string codeGeneratorTemplate                     { get { return null; } }
        public virtual bool codeGeneratorCompute                        { get { return true; } }
        public virtual VFXContextType contextType                       { get { return m_ContextType; } }
        public virtual VFXDataType inputType                            { get { return m_InputType; } }
        public virtual VFXDataType outputType                           { get { return m_OutputType; } }
        public virtual VFXDataType ownedType                            { get { return contextType == VFXContextType.Output ? inputType : outputType; } }
        public virtual VFXTaskType taskType                             { get { return VFXTaskType.None; } }
        public virtual IEnumerable<VFXAttributeInfo> attributes         { get { return Enumerable.Empty<VFXAttributeInfo>(); } }
        public virtual IEnumerable<VFXMapping> additionalMappings       { get { return Enumerable.Empty<VFXMapping>(); } }
        public virtual IEnumerable<string> additionalDefines            { get { return Enumerable.Empty<string>(); } }
        public virtual IEnumerable<KeyValuePair<string, VFXShaderWriter>> additionalReplacements { get { return Enumerable.Empty<KeyValuePair<string, VFXShaderWriter>>(); } }

        public virtual bool CanBeCompiled()
        {
            return m_Data != null && m_Data.CanBeCompiled();
        }

        public void MarkAsCompiled(bool compiled)
        {
            hasBeenCompiled = compiled;
        }

        public override void CollectDependencies(HashSet<ScriptableObject> objs, bool compileOnly = false)
        {
            base.CollectDependencies(objs,compileOnly);
            if (m_Data != null)
            {
                objs.Add(m_Data);
                m_Data.CollectDependencies(objs,compileOnly);
            }
        }

        protected override void OnInvalidate(VFXModel model, InvalidationCause cause)
        {
            base.OnInvalidate(model, cause);

            if (cause == InvalidationCause.kStructureChanged ||
                cause == InvalidationCause.kConnectionChanged ||
                cause == InvalidationCause.kExpressionInvalidated ||
                cause == InvalidationCause.kSettingChanged)
            {
                if (hasBeenCompiled || CanBeCompiled())
                    Invalidate(InvalidationCause.kExpressionGraphChanged);
            }
        }

        public override bool AcceptChild(VFXModel model, int index = -1)
        {
            if (!base.AcceptChild(model, index))
                return false;

            var block = (VFXBlock)model;
            return Accept(block, index);
        }

        public virtual bool Accept(VFXBlock block, int index = -1)
        {
            var testedType = contextType == VFXContextType.Output ? inputType : outputType;
            return ((block.compatibleContexts & contextType) != 0) && ((block.compatibleData & testedType) != 0);
        }

        protected override void OnAdded()
        {
            base.OnAdded();
            if (hasBeenCompiled || CanBeCompiled())
                Invalidate(InvalidationCause.kExpressionGraphChanged);
        }

        protected override void OnRemoved()
        {
            base.OnRemoved();
            if (hasBeenCompiled || CanBeCompiled())
                Invalidate(InvalidationCause.kExpressionGraphChanged);
        }

        public static bool CanLink(VFXContext from, VFXContext to, int fromIndex = 0, int toIndex = 0)
        {
            if (from == to || from == null || to == null)
                return false;

            if (from.outputType == VFXDataType.None || to.inputType == VFXDataType.None || from.outputType != to.inputType)
                return false;

            if (fromIndex >= from.outputFlowSlot.Length || toIndex >= to.inputFlowSlot.Length)
                return false;

            //If link already present, returns false
            if (    from.m_OutputFlowSlot[fromIndex].link   .Any(o => o.context == to   && o.slotIndex == toIndex)
                ||  to.m_InputFlowSlot[toIndex].link        .Any(o => o.context == from && o.slotIndex == fromIndex))
                return false;

            return true;
        }

        public void LinkTo(VFXContext context, int fromIndex = 0, int toIndex = 0)
        {
            InnerLink(this, context, fromIndex, toIndex);
        }

        public void LinkFrom(VFXContext context, int fromIndex = 0, int toIndex = 0)
        {
            InnerLink(context, this, fromIndex, toIndex);
        }

        public void UnlinkTo(VFXContext context, int fromIndex = 0, int toIndex = 0)
        {
            InnerUnlink(this, context, fromIndex, toIndex);
        }

        public void UnlinkFrom(VFXContext context, int fromIndex = 0, int toIndex = 0)
        {
            InnerUnlink(context, this, fromIndex, toIndex);
        }

        public void UnlinkAll()
        {
            for (int slot = 0; slot < m_OutputFlowSlot.Length; slot++)
            {
                while (m_OutputFlowSlot[slot].link.Count > 0)
                {
                    var clean = m_OutputFlowSlot[slot].link.Last();
                    InnerUnlink(this, clean.context, slot, clean.slotIndex);
                }
            }

            for (int slot = 0; slot < m_InputFlowSlot.Length; slot++)
            {
                while (m_InputFlowSlot[slot].link.Count > 0)
                {
                    var clean = m_InputFlowSlot[slot].link.Last();
                    InnerUnlink(clean.context, this, clean.slotIndex, slot);
                }
            }
        }

        private bool CanLinkToMany()
        {
            return contextType == VFXContextType.Spawner
                || contextType == VFXContextType.Event;
        }

        private bool CanLinkFromMany()
        {
            return contextType == VFXContextType.Output
                || contextType == VFXContextType.Spawner
                || contextType == VFXContextType.Subgraph
                ||  contextType == VFXContextType.Init;
        }

        private static bool IsExclusiveLink(VFXContextType from, VFXContextType to)
        {
            if (from == to)
                return false;
            if (from == VFXContextType.Spawner)
                return false;
            return true;
        }

        private static void InnerLink(VFXContext from, VFXContext to, int fromIndex, int toIndex, bool notify = true)
        {
            if (!CanLink(from, to, fromIndex, toIndex))
                throw new ArgumentException(string.Format("Cannot link contexts {0} and {1}", from, to));

            // Handle constraints on connections
            foreach (var link in from.m_OutputFlowSlot[fromIndex].link.ToArray())
            {
                if (!link.context.CanLinkFromMany() || (IsExclusiveLink(link.context.contextType, to.contextType) && from.contextType == link.context.contextType))
                {
                    if (link.context.inputFlowCount > toIndex)
                        InnerUnlink(from, link.context, fromIndex, toIndex, notify);
                }
            }

            foreach (var link in to.m_InputFlowSlot[toIndex].link.ToArray())
            {
                if (!link.context.CanLinkToMany() || IsExclusiveLink(link.context.contextType, from.contextType))
                {
                    InnerUnlink(link.context, to, fromIndex, toIndex, notify);
                }
            }

            if (from.ownedType == to.ownedType)
                to.InnerSetData(from.GetData(), false);

            from.m_OutputFlowSlot[fromIndex].link.Add(new VFXContextLink() { context = to, slotIndex = toIndex });
            to.m_InputFlowSlot[toIndex].link.Add(new VFXContextLink() { context = from, slotIndex = fromIndex });

            if (notify)
            {
                // TODO Might need a specific event ?
                from.Invalidate(InvalidationCause.kStructureChanged);
                to.Invalidate(InvalidationCause.kStructureChanged);
            }
        }

        private static void InnerUnlink(VFXContext from, VFXContext to, int fromIndex = 0, int toIndex = 0, bool notify = true)
        {
            if (from.ownedType == to.ownedType)
                to.SetDefaultData(false);

            from.m_OutputFlowSlot[fromIndex].link.RemoveAll(o => o.context == to && o.slotIndex == toIndex);
            to.m_InputFlowSlot[toIndex].link.RemoveAll(o => o.context == from && o.slotIndex == fromIndex);

            // TODO Might need a specific event ?
            if (notify)
            {
                from.Invalidate(InvalidationCause.kStructureChanged);
                to.Invalidate(InvalidationCause.kStructureChanged);
            }
        }

        public VFXContextSlot[] inputFlowSlot { get { return m_InputFlowSlot == null ? new VFXContextSlot[] {} : m_InputFlowSlot; } }
        public VFXContextSlot[] outputFlowSlot { get { return m_OutputFlowSlot == null ? new VFXContextSlot[] {} : m_OutputFlowSlot; } }
        protected virtual int inputFlowCount { get { return 1; } }
        protected virtual int outputFlowCount { get { return 1; } }

        public IEnumerable<VFXContext> inputContexts    { get { return m_InputFlowSlot.SelectMany(l => l.link.Select(o => o.context)); } }
        public IEnumerable<VFXContext> outputContexts   { get { return m_OutputFlowSlot.SelectMany(l => l.link.Select(o => o.context)); } }

        public virtual VFXExpressionMapper GetExpressionMapper(VFXDeviceTarget target)
        {
            if (target == VFXDeviceTarget.GPU)
                return VFXExpressionMapper.FromContext(this);

            return null;
        }

        public void SetDefaultData(bool notify)
        {
            InnerSetData(VFXData.CreateDataType(GetGraph(),ownedType), notify);
        }

        public virtual void OnDataChanges(VFXData oldData, VFXData newData)
        {
        }

        private void InnerSetData(VFXData data, bool notify)
        {
            if (m_Data != data)
            {
                if (m_Data != null)
                {
                    m_Data.OnContextRemoved(this);
                    if (m_Data.owners.Count() == 0)
                        m_Data.Detach();
                }
                OnDataChanges(m_Data, data);
                m_Data = data;

                if (m_Data != null)
                    m_Data.OnContextAdded(this);

                if (notify)
                    Invalidate(InvalidationCause.kStructureChanged);

                // Propagate data downwards
                foreach (var output in m_OutputFlowSlot.SelectMany(o => o.link.Select(l => l.context)))
                    if (output.ownedType == ownedType)
                        output.InnerSetData(data, notify);
            }
        }

        public VFXData GetData()
        {
            return m_Data;
        }

        protected virtual IEnumerable<VFXBlock> implicitPreBlock
        {
            get
            {
                return Enumerable.Empty<VFXBlock>();
            }
        }

        protected virtual IEnumerable<VFXBlock> implicitPostBlock
        {
            get
            {
                return Enumerable.Empty<VFXBlock>();
            }
        }

        public IEnumerable<VFXBlock> activeChildrenWithImplicit
        {
            get
            {
                return implicitPreBlock.Concat(children).Concat(implicitPostBlock).Where(o => o.enabled);
            }
        }

        public IEnumerable<VFXBlock> activeFlattenedChildrenWithImplicit
        {
            get{
                return implicitPreBlock.Concat(children.SelectMany(t => t is VFXSubgraphBlock ? (t.enabled ? (t as VFXSubgraphBlock).recusiveSubBlocks : Enumerable.Empty<VFXBlock>()): Enumerable.Repeat(t, 1))).Concat(implicitPostBlock).Where(o => o.enabled);
            }
        }

        private IEnumerable<IVFXSlotContainer> allSlotContainer
        {
            get
            {
                return activeFlattenedChildrenWithImplicit.OfType<IVFXSlotContainer>().Concat(Enumerable.Repeat(this as IVFXSlotContainer, 1));
            }
        }

        public IEnumerable<VFXSlot> allLinkedOutputSlot
        {
            get
            {
                return allSlotContainer.SelectMany(o => o.outputSlots.SelectMany(s => s.LinkedSlots));
            }
        }

        public IEnumerable<VFXSlot> allLinkedInputSlot
        {
            get
            {
                return allSlotContainer.SelectMany(o => o.inputSlots.SelectMany(s => s.LinkedSlots));
            }
        }

        // Not serialized nor exposed
        private VFXContextType m_ContextType;
        private VFXDataType m_InputType;
        private VFXDataType m_OutputType;

        [NonSerialized]
        private bool hasBeenCompiled = false;

        [SerializeField]
        private VFXData m_Data;

        [SerializeField]
        private VFXContextSlot[] m_InputFlowSlot;
        [SerializeField]
        private VFXContextSlot[] m_OutputFlowSlot;

        public char letter { get; set; }


        string shaderNamePrefix = "Hidden/VFX";

        public string shaderName
        {
            get
            {
                string assetName = string.Empty;
                try
                {
                    assetName = GetGraph().visualEffectResource.asset.name;
                }
                catch(Exception e)
                {
                    Debug.LogException(e, this);
                }

                string prefix = shaderNamePrefix + (assetName == string.Empty? "" : "/"+assetName);
                if (GetData() != null)
                {
                    string dataName = GetData().fileName;
                    if (!string.IsNullOrEmpty(dataName))
                        prefix += "/" + dataName;
                }

                if (letter != '\0')
                {
                    if (string.IsNullOrEmpty(label))
                        return string.Format("{2}/({0}) {1}", letter, libraryName, prefix);
                    else
                        return string.Format("{2}/({0}) {1}", letter, label, prefix);
                }
                else
                {
                    if (string.IsNullOrEmpty(label))
                        return string.Format("{1}/{0}", libraryName, prefix);
                    else
                        return string.Format("{1}/{0}",label, prefix);
                }
            }
        }
        public string fileName
        {
            get
            {
                string prefix = string.Empty;
                if (GetData() != null)
                {
                    string dataName = GetData().fileName;
                    if (!string.IsNullOrEmpty(dataName))
                        prefix += "[" + dataName + "]";
                }

                if (letter != '\0')
                {
                    if (string.IsNullOrEmpty(label))
                        return string.Format("{2}{0} {1}", letter, libraryName, prefix);
                    else
                        return string.Format("{2}{0} {1}", letter, label, prefix);
                }
                else
                {
                    if (string.IsNullOrEmpty(label))
                        return string.Format("{1}{0}", libraryName, prefix);
                    else
                        return string.Format("{1}{0}", label, prefix);
                }
            }
        }

        public override VFXCoordinateSpace GetOutputSpaceFromSlot(VFXSlot slot)
        {
            return space;
        }

        public bool spaceable
        {
            get
            {
                return m_Data is ISpaceable;
            }
        }

        public VFXCoordinateSpace space
        {
            get
            {
                if (spaceable)
                {
                    return (m_Data as ISpaceable).space;
                }
                return VFXCoordinateSpace.Local;
            }

            set
            {
                if (spaceable)
                {
                    (m_Data as ISpaceable).space = value;
                    foreach (var owner in m_Data.owners)
                        Invalidate(InvalidationCause.kSettingChanged);

                    var allSlots = m_Data.owners.SelectMany(c => c.inputSlots.Concat(c.activeFlattenedChildrenWithImplicit.SelectMany(o => o.inputSlots)));
                    foreach (var slot in allSlots.Where(s => s.spaceable))
                        slot.Invalidate(InvalidationCause.kSpaceChanged);
                }
            }
        }
    }
}
