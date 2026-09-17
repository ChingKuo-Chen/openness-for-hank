using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

// Turns a compact rung description into valid SimaticML LAD so blocks can be
// authored from scratch. The instruction table below was measured from real
// exported blocks: port names differ per instruction in ways that are not
// guessable (comparators take power flow on "pre", maths on "en").
internal static class LadWriter
{
    private const string FlgNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    private const string IfNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    private sealed class Instruction
    {
        public string PowerIn;          // port that receives power flow
        public string PowerOut;         // port that passes power flow on
        public string Version;          // required for firmware instructions
        public bool NeedsInstance;      // timers and counters carry an instance
        public string[] TypeTemplates;  // TemplateValue Name entries of Type kind
        public string CardTemplate;     // TemplateValue Name of Cardinality kind
        public string InstanceScope;    // LocalVariable for multi-instance, GlobalVariable for its own DB
    }

    // Library instructions carry a mandatory Version and their own instance data.
    // Versions and instance scopes below were read out of the exported blocks.
    private static Instruction Lib(string version, string scope)
    {
        return new Instruction
        {
            PowerIn = "en",
            PowerOut = "eno",
            Version = version,
            NeedsInstance = scope != null,
            InstanceScope = scope,
        };
    }

    private static readonly Dictionary<string, Instruction> Table =
        new Dictionary<string, Instruction>(StringComparer.OrdinalIgnoreCase)
    {
        { "TON",       new Instruction { PowerIn = "IN",  PowerOut = "Q",   Version = "1.0", NeedsInstance = true,  TypeTemplates = new[] { "time_type" } } },
        { "TOF",       new Instruction { PowerIn = "IN",  PowerOut = "Q",   Version = "1.0", NeedsInstance = true,  TypeTemplates = new[] { "time_type" } } },
        { "TP",        new Instruction { PowerIn = "IN",  PowerOut = "Q",   Version = "1.0", NeedsInstance = true,  TypeTemplates = new[] { "time_type" } } },

        { "Gt",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "Lt",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "Ge",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "Le",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "Eq",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "Ne",        new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "InRange",   new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },
        { "OutRange",  new Instruction { PowerIn = "pre", PowerOut = "out", TypeTemplates = new[] { "SrcType" } } },

        { "Add",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" }, CardTemplate = "Card" } },
        { "Sub",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" } } },
        { "Mul",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" }, CardTemplate = "Card" } },
        { "Div",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" } } },
        { "Abs",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" } } },
        { "Neg",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" } } },
        { "Inc",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "DestType" } } },
        { "Dec",       new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "DestType" } } },

        { "Move",      new Instruction { PowerIn = "en",  PowerOut = "eno", CardTemplate = "Card" } },
        { "Convert",   new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType", "DestType" } } },
        { "Round",     new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType", "DestType" } } },
        { "Scale_X",   new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType", "DestType" } } },
        { "Normalize", new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType", "DestType" } } },
        { "LIMIT",     new Instruction { PowerIn = "en",  PowerOut = "eno", Version = "1.0", TypeTemplates = new[] { "value_type" } } },
        { "MIN",       new Instruction { PowerIn = "en",  PowerOut = "eno", Version = "1.0", TypeTemplates = new[] { "value_type" }, CardTemplate = "card" } },
        { "MAX",       new Instruction { PowerIn = "en",  PowerOut = "eno", Version = "1.0", TypeTemplates = new[] { "value_type" }, CardTemplate = "card" } },
        { "Calc",      new Instruction { PowerIn = "en",  PowerOut = "eno", TypeTemplates = new[] { "SrcType" }, CardTemplate = "Card" } },

        { "CTRL_HSC",         Lib("1.0", "LocalVariable") },
        { "High_Speed_Counter", Lib("4.1", "GlobalVariable") },
        { "MB_CLIENT",        Lib("6.0", "GlobalVariable") },
        { "MB_MASTER",        Lib("2.1", "LocalVariable") },
        { "MB_SLAVE",         Lib("2.1", "GlobalVariable") },
        { "MB_COMM_LOAD",     Lib("2.1", "GlobalVariable") },
        { "MB_SERVER",        Lib("5.3", "GlobalVariable") },
        { "Modbus_Master",    Lib("5.1", "LocalVariable") },
        { "Modbus_Comm_Load", Lib("5.0", "LocalVariable") },
        { "MC_Power",         Lib("8.0", "LocalVariable") },
        { "MC_Reset",         Lib("8.0", "LocalVariable") },
        { "MC_MoveJog",       Lib("8.0", "LocalVariable") },
        { "RD_LOC_T", new Instruction { PowerIn = "en", PowerOut = "eno", Version = "1.0", TypeTemplates = new[] { "date_type" } } },
        // RDREC/WRREC keep multi-instance data and typed templates (measured).
        { "RDREC", new Instruction {
            PowerIn = "en", PowerOut = "eno", Version = "1.0", NeedsInstance = true,
            InstanceScope = "LocalVariable",
            TypeTemplates = new[] { "ptr_type", "id_type", "index_type", "len_type" } } },
        { "WRREC", new Instruction {
            PowerIn = "en", PowerOut = "eno", Version = "1.1", NeedsInstance = true,
            InstanceScope = "LocalVariable",
            TypeTemplates = new[] { "ptr_type", "id_type", "index_type", "len_type" } } },
    };

    private sealed class Net
    {
        public readonly List<XElement> Parts = new List<XElement>();
        public readonly List<XElement> Wires = new List<XElement>();
        public int NextUid = 21;

        public int Take()
        {
            return NextUid++;
        }
    }

    public static string Generate(string specPath)
    {
        XDocument spec = XDocument.Load(specPath);
        XElement root = spec.Root;

        string kind = Attr(root, "kind", "FB");
        string name = Attr(root, "name", null);
        if (name == null)
        {
            throw new InvalidOperationException("規格缺少 name 屬性。");
        }

        XElement block = new XElement("SW.Blocks." + kind, new XAttribute("ID", "0"));

        XElement attributes = new XElement("AttributeList",
            new XElement("AutoNumber", "false"));
        string cyclicTime = Attr(root, "cyclicTime", null);
        if (cyclicTime != null)
        {
            attributes.Add(new XElement("CyclicTime", cyclicTime));
        }

        attributes.Add(
            new XElement("HeaderAuthor", string.Empty),
            new XElement("HeaderFamily", string.Empty),
            new XElement("HeaderName", string.Empty),
            new XElement("HeaderVersion", "1.0"),
            BuildInterface(root),
            new XElement("IsIECCheckEnabled", "false"),
            new XElement("MemoryLayout", Attr(root, "memoryLayout", "Optimized")),
            new XElement("Name", name),
            new XElement("Namespace", string.Empty),
            new XElement("Number", Attr(root, "number", "900")));

        string phaseOffset = Attr(root, "phaseOffset", null);
        if (phaseOffset != null)
        {
            attributes.Add(new XElement("PhaseOffset", phaseOffset));
        }

        attributes.Add(new XElement("ProgrammingLanguage", "LAD"));
        string secondaryType = Attr(root, "secondaryType", null);
        if (secondaryType != null)
        {
            attributes.Add(new XElement("SecondaryType", secondaryType));
        }

        attributes.Add(new XElement("SetENOAutomatically", "false"));

        block.Add(attributes);

        XElement objects = new XElement("ObjectList");
        int id = 1;

        string blockComment = Attr(root, "comment", null);
        if (blockComment != null)
        {
            objects.Add(MultilingualText(ref id, "Comment", blockComment));
        }

        foreach (XElement network in root.Elements("Network"))
        {
            XElement unit = new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", id.ToString("X")),
                new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", BuildNetwork(network)),
                    new XElement("ProgrammingLanguage", "LAD")));

            id++;

            string networkComment = Attr(network, "comment", null);
            string title = Attr(network, "title", null);
            if (networkComment != null || title != null)
            {
                XElement unitObjects = new XElement("ObjectList");
                if (networkComment != null)
                {
                    unitObjects.Add(MultilingualText(ref id, "Comment", networkComment));
                }

                if (title != null)
                {
                    unitObjects.Add(MultilingualText(ref id, "Title", title));
                }

                unit.Add(unitObjects);
            }

            objects.Add(unit);
        }

        block.Add(objects);

        XDocument document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Document",
                new XElement("Engineering", new XAttribute("version", "V21")),
                block));

        return document.Declaration + Environment.NewLine + document.Root;
    }

    // Network titles and comments live in MultilingualText objects with en-US + zh-CN.
    private static XElement MultilingualText(ref int id, string composition, string text)
    {
        XElement items = new XElement("ObjectList");
        int itemId = id + 1;
        foreach (string culture in new[] { "en-US", "zh-CN" })
        {
            items.Add(new XElement("MultilingualTextItem",
                new XAttribute("ID", itemId.ToString("X")),
                new XAttribute("CompositionName", "Items"),
                new XElement("AttributeList",
                    new XElement("Culture", culture),
                    new XElement("Text", text))));
            itemId++;
        }

        XElement node = new XElement("MultilingualText",
            new XAttribute("ID", id.ToString("X")),
            new XAttribute("CompositionName", composition),
            items);

        id = itemId;
        return node;
    }

    private static XElement BuildInterface(XElement root)
    {
        XElement sections = new XElement(XName.Get("Sections", IfNs));
        XElement source = root.Element("Interface");

        string kind = Attr(root, "kind", "FB");
        string[] order = kind == "FC"
            ? new[] { "Input", "Output", "InOut", "Temp", "Constant", "Return" }
            : kind == "OB"
                ? new[] { "Input", "Temp", "Constant" }
                : new[] { "Input", "Output", "InOut", "Static", "Temp", "Constant" };

        foreach (string sectionName in order)
        {
            XElement section = new XElement(XName.Get("Section", IfNs), new XAttribute("Name", sectionName));

            XElement declared = source == null
                ? null
                : source.Elements("Section").FirstOrDefault(e => Attr(e, "name", string.Empty) == sectionName);

            if (declared != null)
            {
                foreach (XElement member in declared.Elements("Member"))
                {
                    section.Add(BuildMember(member));
                }
            }

            if (sectionName == "Return")
            {
                section.Add(new XElement(XName.Get("Member", IfNs),
                    new XAttribute("Name", "Ret_Val"),
                    new XAttribute("Datatype", "Void"),
                    new XAttribute("Accessibility", "Public")));
            }

            sections.Add(section);
        }

        return new XElement("Interface", sections);
    }

    private static XElement BuildMember(XElement source)
    {
        return BuildMember(source, nested: false);
    }

    private static XElement BuildMember(XElement source, bool nested)
    {
        string type = Attr(source, "type", "Bool");

        // A Struct declares its fields as nested Member elements, no Sections wrapper.
        // Nested fields must not carry Accessibility; TIA rejects the import otherwise
        // when the field is itself a structured type such as IEC_TIMER.
        List<XElement> fields = source.Elements("Member").ToList();
        if (fields.Count > 0)
        {
            XElement member = new XElement(XName.Get("Member", IfNs),
                new XAttribute("Name", Attr(source, "name", "member")),
                new XAttribute("Datatype", type));

            if (!nested)
            {
                member.Add(new XAttribute("Accessibility", "Public"));
            }

            foreach (XElement field in fields)
            {
                member.Add(BuildMember(field, nested: true));
            }

            return member;
        }

        return BuildSimpleMember(source, type, nested);
    }

    private static XElement BuildSimpleMember(XElement source, string type, bool nested)
    {
        XElement member = new XElement(XName.Get("Member", IfNs),
            new XAttribute("Name", Attr(source, "name", "member")),
            new XAttribute("Datatype", type));

        string[] nestedLayout = NestedMembersFor(type);
        if (nestedLayout != null)
        {
            member.Add(new XAttribute("Version", "1.0"));
            if (!nested)
            {
                member.Add(new XAttribute("Remanence", "SetInIDB"));
                member.Add(new XAttribute("Accessibility", "Public"));
            }

            XElement inner = new XElement(XName.Get("Section", IfNs), new XAttribute("Name", "None"));
            foreach (string entry in nestedLayout)
            {
                string[] cells = entry.Split(':');
                inner.Add(new XElement(XName.Get("Member", IfNs),
                    new XAttribute("Name", cells[0]),
                    new XAttribute("Datatype", cells[1])));
            }

            member.Add(new XElement(XName.Get("Sections", IfNs), inner));
            return member;
        }

        if (!nested)
        {
            member.Add(new XAttribute("Accessibility", "Public"));
        }

        if (string.Equals(Attr(source, "informative", null), "true", StringComparison.OrdinalIgnoreCase))
        {
            member.Add(new XAttribute("Informative", "true"));
        }

        string initial = Attr(source, "start", null);
        if (initial != null)
        {
            member.Add(new XElement(XName.Get("StartValue", IfNs), initial));
        }

        string note = Attr(source, "comment", null);
        if (note != null)
        {
            member.Add(new XElement(XName.Get("Comment", IfNs),
                new XElement(XName.Get("MultiLanguageText", IfNs),
                    new XAttribute("Lang", "zh-CN"),
                    note),
                new XElement(XName.Get("MultiLanguageText", IfNs),
                    new XAttribute("Lang", "en-US"),
                    note)));
        }

        return member;
    }

    private static string[] NestedMembersFor(string type)
    {
        if (string.Equals(type, "IEC_TIMER", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TON_TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TOF_TIME", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "TP_TIME", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "PT:Time", "ET:Time", "IN:Bool", "Q:Bool" };
        }

        if (string.Equals(type, "IEC_COUNTER", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "CU:Bool", "CD:Bool", "R:Bool", "LD:Bool", "QU:Bool", "QD:Bool", "PV:Int", "CV:Int" };
        }

        return null;
    }

    private static XElement BuildNetwork(XElement source)
    {
        Net net = new Net();

        // Every rung starts at the power rail; the rail wire collects one
        // connection per element that sits at the far left of the network.
        XElement rail = new XElement(XName.Get("Wire", FlgNs), new XElement(XName.Get("Powerrail", FlgNs)));
        List<XElement> railTargets = new List<XElement>();

        foreach (XElement rung in source.Elements())
        {
            Emit(net, rung, railTargets);
        }

        foreach (XElement target in railTargets)
        {
            rail.Add(target);
        }

        // Wire UIds must not clash with part UIds, so allocate them last.
        rail.SetAttributeValue("UId", net.Take().ToString());
        foreach (XElement wire in net.Wires)
        {
            wire.SetAttributeValue("UId", net.Take().ToString());
        }

        XElement flg = new XElement(XName.Get("FlgNet", FlgNs),
            new XElement(XName.Get("Parts", FlgNs), net.Parts),
            new XElement(XName.Get("Wires", FlgNs), new object[] { rail }.Concat(net.Wires)));

        return flg;
    }

    // Returns the connector that carries power flow out of the emitted chain,
    // or null when the chain ends in a coil.
    private static XElement Emit(Net net, XElement node, List<XElement> railTargets)
    {
        switch (node.Name.LocalName)
        {
            case "Series":
                return EmitSeries(net, node, railTargets);
            case "Parallel":
                return EmitParallel(net, node, railTargets);
            case "Fork":
                return EmitFork(net, node, railTargets);
            default:
                return EmitLeaf(net, node, railTargets);
        }
    }

    private static XElement EmitSeries(Net net, XElement node, List<XElement> railTargets)
    {
        XElement flow = null;
        bool first = true;

        foreach (XElement child in node.Elements())
        {
            List<XElement> inlet = new List<XElement>();
            XElement next = Emit(net, child, inlet);

            if (first)
            {
                railTargets.AddRange(inlet);
                first = false;
            }
            else if (inlet.Count > 0)
            {
                // One source connector may appear in exactly one wire, so every
                // destination it feeds has to share that single wire element.
                XElement wire = new XElement(XName.Get("Wire", FlgNs), flow);
                foreach (XElement target in inlet)
                {
                    wire.Add(target);
                }

                net.Wires.Add(wire);
            }

            flow = next;
        }

        return flow;
    }

    // Parallel merges branches through an O part; Fork does the opposite and
    // hands the same power flow to several independent continuations.
    private static XElement EmitFork(Net net, XElement node, List<XElement> railTargets)
    {
        XElement chained = null;

        foreach (XElement branch in node.Elements())
        {
            List<XElement> inlet = new List<XElement>();
            XElement output = Emit(net, branch, inlet);
            railTargets.AddRange(inlet);

            if (output != null && Attr(branch, "chain", "false") == "true")
            {
                chained = output;
            }
        }

        return chained;
    }

    private static XElement EmitParallel(Net net, XElement node, List<XElement> railTargets)
    {
        List<XElement> branchOutputs = new List<XElement>();

        foreach (XElement branch in node.Elements())
        {
            XElement output = Emit(net, branch, railTargets);
            if (output != null)
            {
                branchOutputs.Add(output);
            }
        }

        int uid = net.Take();
        XElement or = new XElement(XName.Get("Part", FlgNs),
            new XAttribute("Name", "O"),
            new XAttribute("UId", uid.ToString()),
            Template("Card", "Cardinality", branchOutputs.Count.ToString(CultureInfo.InvariantCulture)));
        net.Parts.Add(or);

        for (int i = 0; i < branchOutputs.Count; i++)
        {
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                branchOutputs[i],
                NameCon(uid, "in" + (i + 1))));
        }

        return NameCon(uid, "out");
    }

    private static XElement EmitLeaf(Net net, XElement node, List<XElement> railTargets)
    {
        string kind = node.Name.LocalName;

        // Plain contacts take power on "in"; the edge-detecting variants take it
        // on "pre" and additionally need a retentive bit to remember last scan.
        if (kind == "Contact" || kind == "PContact" || kind == "NContact")
        {
            int uid = net.Take();
            XElement part = new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", kind),
                new XAttribute("UId", uid.ToString()));

            if (Attr(node, "negated", "false") == "true")
            {
                part.Add(new XElement(XName.Get("Negated", FlgNs), new XAttribute("Name", "operand")));
            }

            net.Parts.Add(part);
            railTargets.Add(NameCon(uid, kind == "Contact" ? "in" : "pre"));
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, node), NameCon(uid, "operand")));

            if (kind != "Contact")
            {
                net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                    EdgeBit(net, node, kind), NameCon(uid, "bit")));
            }

            return NameCon(uid, "out");
        }

        if (kind == "Coil" || kind == "SCoil" || kind == "RCoil" || kind == "PCoil" || kind == "NCoil")
        {
            int uid = net.Take();
            XElement part = new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", kind),
                new XAttribute("UId", uid.ToString()));

            if (Attr(node, "negated", "false") == "true")
            {
                part.Add(new XElement(XName.Get("Negated", FlgNs), new XAttribute("Name", "operand")));
            }

            net.Parts.Add(part);
            railTargets.Add(NameCon(uid, "in"));
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, node), NameCon(uid, "operand")));

            if (kind == "PCoil" || kind == "NCoil")
            {
                net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                    EdgeBit(net, node, kind), NameCon(uid, "bit")));
            }

            // Coils pass power through on "out" so a series can continue
            // (Blink chains Coil → CoilTON this way).
            return NameCon(uid, "out");
        }

        if (kind == "PBox" || kind == "NBox")
        {
            int uid = net.Take();
            net.Parts.Add(new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", kind),
                new XAttribute("UId", uid.ToString())));

            railTargets.Add(NameCon(uid, "in"));
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                EdgeBit(net, node, kind), NameCon(uid, "bit")));

            return NameCon(uid, "out");
        }

        if (kind == "Not")
        {
            int uid = net.Take();
            net.Parts.Add(new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", "Not"),
                new XAttribute("UId", uid.ToString())));

            railTargets.Add(NameCon(uid, "in"));
            return NameCon(uid, "out");
        }

        // Set/reset latch. The reference project always ends a rung on it, so the
        // q output is only wired when the spec explicitly asks for it.
        if (kind == "Sr" || kind == "Rs")
        {
            int uid = net.Take();
            net.Parts.Add(new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", kind),
                new XAttribute("UId", uid.ToString())));

            string setPort = kind == "Sr" ? "s" : "r";
            string otherPort = kind == "Sr" ? "r1" : "s1";

            railTargets.Add(NameCon(uid, setPort));
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, node), NameCon(uid, "operand")));

            XElement other = node.Elements("In")
                .FirstOrDefault(item => Attr(item, "port", otherPort) == otherPort);
            if (other == null)
            {
                throw new InvalidOperationException(kind + " 需要 <In port=\"" + otherPort + "\" .../>。");
            }

            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, other), NameCon(uid, otherPort)));

            return Attr(node, "chain", "false") == "true" ? NameCon(uid, "q") : null;
        }

        // Timer in coil form: power flows in on "in", the duration sits on "value".
        if (kind == "CoilTON" || kind == "CoilTOF" || kind == "CoilTP")
        {
            int uid = net.Take();
            net.Parts.Add(new XElement(XName.Get("Part", FlgNs),
                new XAttribute("Name", kind),
                new XAttribute("UId", uid.ToString()),
                Template("time_type", "Type", Attr(node, "time_type", "Time"))));

            railTargets.Add(NameCon(uid, "in"));
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, node), NameCon(uid, "operand")));

            XElement duration = node.Elements("In")
                .FirstOrDefault(item => Attr(item, "port", "value") == "value");
            if (duration == null)
            {
                throw new InvalidOperationException(kind + " 需要 <In port=\"value\" time=\"...\" />。");
            }

            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, duration), NameCon(uid, "value")));

            return null;
        }

        if (kind == "Call")
        {
            return EmitCall(net, node, railTargets);
        }

        if (kind == "Box")
        {
            return EmitBox(net, node, railTargets);
        }

        throw new InvalidOperationException("不認識的元件：" + kind);
    }

    // A block call is not a Part: it is a sibling <Call> element whose CallInfo
    // repeats the callee's interface, one <Parameter> per argument in order.
    private static XElement EmitCall(Net net, XElement node, List<XElement> railTargets)
    {
        string target = Attr(node, "name", null);
        if (target == null)
        {
            throw new InvalidOperationException("Call 缺少 name 屬性。");
        }

        string blockType = Attr(node, "type", "FB");
        int uid = net.Take();

        XElement callInfo = new XElement(XName.Get("CallInfo", FlgNs),
            new XAttribute("Name", target),
            new XAttribute("BlockType", blockType));

        if (blockType == "FB")
        {
            string instance = Attr(node, "instance", null);
            if (instance == null)
            {
                throw new InvalidOperationException("呼叫 FB " + target + " 需要 instance 屬性。");
            }

            // A global DB is its own instance; a member of the caller is a multi-instance.
            string scope = Attr(node, "scope", "GlobalVariable");
            callInfo.Add(new XElement(XName.Get("Instance", FlgNs),
                new XAttribute("Scope", scope),
                new XAttribute("UId", net.Take().ToString()),
                PathComponents(instance)));
        }

        List<XElement> arguments = node.Elements("Param").ToList();
        foreach (XElement argument in arguments)
        {
            callInfo.Add(new XElement(XName.Get("Parameter", FlgNs),
                new XAttribute("Name", Attr(argument, "name", "param")),
                new XAttribute("Section", Attr(argument, "section", "Input")),
                new XAttribute("Type", Attr(argument, "type", "Bool"))));
        }

        net.Parts.Add(new XElement(XName.Get("Call", FlgNs),
            new XAttribute("UId", uid.ToString()),
            callInfo));

        railTargets.Add(NameCon(uid, "en"));

        foreach (XElement argument in arguments)
        {
            string section = Attr(argument, "section", "Input");
            string port = Attr(argument, "name", "param");

            if (Attr(argument, "var", null) == null &&
                Attr(argument, "tag", null) == null &&
                Attr(argument, "globalconst", null) == null &&
                Attr(argument, "const", null) == null &&
                Attr(argument, "time", null) == null)
            {
                continue;
            }

            if (section == "Output")
            {
                net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                    NameCon(uid, port), Operand(net, argument)));
            }
            else
            {
                net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                    Operand(net, argument), NameCon(uid, port)));
            }
        }

        return NameCon(uid, "eno");
    }

    private static XElement EmitBox(Net net, XElement node, List<XElement> railTargets)
    {
        string name = Attr(node, "name", null);
        if (name == null || !Table.ContainsKey(name))
        {
            throw new InvalidOperationException("指令表沒有這個指令：" + name);
        }

        Instruction spec = Table[name];
        int uid = net.Take();

        XElement part = new XElement(XName.Get("Part", FlgNs),
            new XAttribute("Name", name),
            new XAttribute("UId", uid.ToString()));

        if (spec.Version != null)
        {
            part.SetAttributeValue("Version", spec.Version);
        }

        if (Attr(node, "disabledeno", "false") == "true" ||
            Attr(node, "eno", "true") == "false")
        {
            part.SetAttributeValue("DisabledENO", "true");
        }

        if (spec.NeedsInstance)
        {
            string instance = Attr(node, "instance", null);
            if (instance == null)
            {
                throw new InvalidOperationException(name + " 需要 instance 屬性。");
            }

            part.Add(new XElement(XName.Get("Instance", FlgNs),
                new XAttribute("Scope", Attr(node, "scope", spec.InstanceScope ?? "LocalVariable")),
                new XAttribute("UId", net.Take().ToString()),
                PathComponents(instance)));
        }

        // Equation must precede TemplateValue in the schema order.
        if (name == "Calc")
        {
            part.Add(new XElement(XName.Get("Equation", FlgNs), Attr(node, "equation", "IN1")));
        }

        List<XElement> inputs = node.Elements("In").ToList();
        List<XElement> outputs = node.Elements("Out").ToList();

        if (spec.CardTemplate != null)
        {
            int card = inputs.Count > 0 ? inputs.Count : 1;
            string declared = Attr(node, "card", null);
            if (declared != null)
            {
                card = int.Parse(declared, CultureInfo.InvariantCulture);
            }

            part.Add(Template(spec.CardTemplate, "Cardinality", card.ToString(CultureInfo.InvariantCulture)));
        }

        if (spec.TypeTemplates != null)
        {
            foreach (string template in spec.TypeTemplates)
            {
                string value = Attr(node, template, null) ?? Attr(node, "type", null);
                if (value == null)
                {
                    throw new InvalidOperationException(name + " 需要 " + template + " 或 type 屬性。");
                }

                part.Add(Template(template, "Type", value));
            }
        }

        net.Parts.Add(part);
        railTargets.Add(NameCon(uid, spec.PowerIn));

        foreach (XElement input in inputs)
        {
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                Operand(net, input), NameCon(uid, Attr(input, "port", "in"))));
        }

        foreach (XElement output in outputs)
        {
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                NameCon(uid, Attr(output, "port", "out")), Operand(net, output)));
        }

        // TIA rejects a box whose declared outputs are left dangling, so any
        // port named in the spec as open gets an explicit open connector.
        foreach (XElement open in node.Elements("Open"))
        {
            net.Wires.Add(new XElement(XName.Get("Wire", FlgNs),
                NameCon(uid, Attr(open, "port", "eno")),
                new XElement(XName.Get("OpenCon", FlgNs), new XAttribute("UId", net.Take().ToString()))));
        }

        return NameCon(uid, spec.PowerOut);
    }

    // Edge instructions store last scan's state in their own bit, declared on the
    // element as mem="..." so it stays visible in the spec.
    private static XElement EdgeBit(Net net, XElement node, string kind)
    {
        string memory = Attr(node, "mem", null);
        if (memory == null)
        {
            throw new InvalidOperationException(kind + " 需要 mem 屬性（邊緣記憶位元）。");
        }

        return Operand(net, new XElement("Mem", new XAttribute("var", memory)));
    }

    private static XElement[] PathComponents(string path)
    {
        return path.Split('.')
            .Select(part => new XElement(XName.Get("Component", FlgNs), new XAttribute("Name", part)))
            .ToArray();
    }

    private static XElement Operand(Net net, XElement node)
    {
        int uid = net.Take();
        XElement access;

        string variable = Attr(node, "var", null);
        string global = Attr(node, "tag", null);
        string globalConstant = Attr(node, "globalconst", null);
        string constant = Attr(node, "const", null);
        string time = Attr(node, "time", null);

        if (variable != null)
        {
            access = new XElement(XName.Get("Access", FlgNs),
                new XAttribute("Scope", "LocalVariable"),
                Symbol(variable));
        }
        else if (global != null)
        {
            access = new XElement(XName.Get("Access", FlgNs),
                new XAttribute("Scope", "GlobalVariable"),
                Symbol(global));
        }
        else if (globalConstant != null)
        {
            access = new XElement(XName.Get("Access", FlgNs),
                new XAttribute("Scope", "GlobalConstant"),
                new XElement(XName.Get("Constant", FlgNs),
                    new XAttribute("Name", globalConstant)));
        }
        else if (time != null)
        {
            // Duration literals carry no ConstantType in the exported format.
            access = new XElement(XName.Get("Access", FlgNs),
                new XAttribute("Scope", "TypedConstant"),
                new XElement(XName.Get("Constant", FlgNs),
                    new XElement(XName.Get("ConstantValue", FlgNs), time)));
        }
        else if (constant != null)
        {
            access = new XElement(XName.Get("Access", FlgNs),
                new XAttribute("Scope", "LiteralConstant"),
                new XElement(XName.Get("Constant", FlgNs),
                    new XElement(XName.Get("ConstantType", FlgNs), Attr(node, "type", "Int")),
                    new XElement(XName.Get("ConstantValue", FlgNs), constant)));
        }
        else
        {
            throw new InvalidOperationException("元件缺少 var/tag/globalconst/const/time 其中一個：" + node.Name.LocalName);
        }

        access.SetAttributeValue("UId", uid.ToString());
        net.Parts.Add(access);
        return new XElement(XName.Get("IdentCon", FlgNs), new XAttribute("UId", uid.ToString()));
    }

    private static XElement Symbol(string path)
    {
        XElement symbol = new XElement(XName.Get("Symbol", FlgNs));
        foreach (string part in path.Split('.'))
        {
            int slice = part.IndexOf(":x", StringComparison.OrdinalIgnoreCase);
            if (slice > 0)
            {
                string name = part.Substring(0, slice);
                string modifier = part.Substring(slice + 1); // x0, x1, ...
                symbol.Add(new XElement(XName.Get("Component", FlgNs),
                    new XAttribute("Name", name),
                    new XAttribute("SliceAccessModifier", modifier)));
                continue;
            }

            int bracket = part.IndexOf('[');
            if (bracket > 0 && part.EndsWith("]", StringComparison.Ordinal))
            {
                string name = part.Substring(0, bracket);
                string index = part.Substring(bracket + 1, part.Length - bracket - 2);
                XElement component = new XElement(XName.Get("Component", FlgNs),
                    new XAttribute("Name", name),
                    new XAttribute("AccessModifier", "Array"),
                    new XElement(XName.Get("Access", FlgNs),
                        new XAttribute("Scope", "LiteralConstant"),
                        new XElement(XName.Get("Constant", FlgNs),
                            new XElement(XName.Get("ConstantType", FlgNs), "DInt"),
                            new XElement(XName.Get("ConstantValue", FlgNs), index))));
                symbol.Add(component);
            }
            else
            {
                symbol.Add(new XElement(XName.Get("Component", FlgNs), new XAttribute("Name", part)));
            }
        }

        return symbol;
    }

    private static XElement NameCon(int uid, string port)
    {
        return new XElement(XName.Get("NameCon", FlgNs),
            new XAttribute("UId", uid.ToString()),
            new XAttribute("Name", port));
    }

    private static XElement Template(string name, string type, string value)
    {
        return new XElement(XName.Get("TemplateValue", FlgNs),
            new XAttribute("Name", name),
            new XAttribute("Type", type),
            value);
    }

    private static string Attr(XElement node, string name, string fallback)
    {
        XAttribute attribute = node.Attribute(name);
        return attribute == null ? fallback : attribute.Value;
    }
}
