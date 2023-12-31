using ImGuiNET;
using System;

namespace Navmesh;

public class UITree
{
    private uint _selectedId;

    public struct NodeRaii : IDisposable
    {
        public bool Selected { get; init; }
        public bool Opened { get; init; }
        public bool Hovered { get; init; }
        private bool _disposed;
        private bool _realOpened;

        public bool SelectedOrHovered => Selected || Hovered;

        public NodeRaii(bool selected, bool opened, bool hovered, bool realOpened)
        {
            Selected = selected;
            Opened = opened;
            Hovered = hovered;
            _realOpened = realOpened;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            if (_realOpened)
                ImGui.TreePop();
            ImGui.PopID();
            _disposed = true;
        }
    }

    public NodeRaii Node(string text, bool leaf = false, uint color = 0xffffffff)
    {
        var id = ImGui.GetID(text);
        var flags = ImGuiTreeNodeFlags.None;
        if (id == _selectedId)
            flags |= ImGuiTreeNodeFlags.Selected;
        if (leaf)
            flags |= ImGuiTreeNodeFlags.Leaf;

        ImGui.PushID((int)id);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        bool open = ImGui.TreeNodeEx(text, flags);
        ImGui.PopStyleColor();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            _selectedId = id;
        return new(id == _selectedId, open && !leaf, ImGui.IsItemHovered(), open);
    }

    // returned node is auto disposed
    public NodeRaii LeafNode(string text, uint color = 0xffffffff)
    {
        var n = Node(text, true, color);
        n.Dispose();
        return n;
    }
}
