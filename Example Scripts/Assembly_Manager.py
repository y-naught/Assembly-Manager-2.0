"""
NOTE:

- Reference to RhinoCommmon.dll is added by default

- You can specify your script requirements like:

    # r: <package-specifier> [, <package-specifier>]
    # requirements: <package-specifier> [, <package-specifier>]

    For example this line will ask the runtime to install
    the listed packages before running the script:

    # requirements: pytoml, keras

    You can install specific versions of a package
    using pip-like package specifiers:

    # r: pytoml==0.10.2, keras>=2.6.0

- Use env directive to add an environment path to sys.path automatically
    # env: /path/to/your/site-packages/
"""
#! python3

import rhinoscriptsyntax as rs
import scriptcontext as sc
import math

import System
import System.Collections.Generic
import Rhino
import Rhino.UI
import Eto.Drawing as drawing
import Eto.Forms as forms
import Create_Assembly as ca
import categorize_parts as cp
import Lay_Parts_Flat as lp
import Orient as orient


# Class for the Assembly Manager Dialog
class AssemblyManagerEtoDialog(forms.Dialog[bool]):

    # updates the list box with components and parts after an assembly is selected
    def update_component_list_box(self, sender, e):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        if(len(assemblies) > assembly_index and assembly_index != -1):
            assembly_name = assemblies[assembly_index]
            temp_data = ca.getAssemblyData(assembly_name)
            self.component_list.DataStore = temp_data[1]
            self.update_parts_no_click()
        else:
            print("index not valid")


    def initialize_component_list_box(self):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        if(assembly_index != -1):
            assembly_name = assemblies[assembly_index]
            temp_data = ca.getAssemblyData(assembly_name)
            return temp_data[1]
        else:
            return None


    def populate_assembly_list_box():
        assemblies = ca.getAssemblies()
        for assembly in assemblies:
            assembly_info = ca.getAssemblyData(assembly)


    def update_assembly_list(self):
        self.assembly_list.SelectedIndex = 0
        self.assembly_list.DataStore = ca.getAssemblies()
        

    def update_component_list(self, assembly_name):
        self.component_list.DataStore = ca.getComponents(assembly_name)

    def update_parts_details(self, sender, e):
        print("Updating part details")


    def update_parts_list_box(self, sender, e):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        if(len(assemblies) > assembly_index and assembly_index != -1):
            assembly_name = assemblies[assembly_index]
            temp_data = ca.getAssemblyData(assembly_name)
            component_index = self.component_list.SelectedIndex
            component_name = temp_data[1][component_index]
            component_data = ca.getCompData(assembly_name, component_name)
            culled_list = component_data[1]
            self.part_list.DataStore = culled_list
            if(component_data[2] != None):
                self.component_quantity_textbox.Text = str(component_data[2])
            


    def update_parts_no_click(self):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        if((len(assemblies) > assembly_index) and assembly_index != -1):
            assembly_name = assemblies[assembly_index]
            temp_data = ca.getAssemblyData(assembly_name)
            component_index = self.component_list.SelectedIndex
            if(len(temp_data[1]) >= component_index or assembly_index != -1):
                self.component_list.SelectedIndex = 0
                component_index = 0
            component_name = temp_data[1][component_index]
            print(component_name)
            component_data = ca.getCompData(assembly_name, component_name)
            culled_list = component_data[1]
            self.part_list.DataStore = culled_list
            if(component_data[2] != None):
                self.component_quantity_textbox.Text = str(component_data[2])

    def initialize_parts_list_box(self):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        if(assembly_index != -1):
            assembly_name = assemblies[assembly_index]
            temp_data = ca.getAssemblyData(assembly_name)
            component_index = self.component_list.SelectedIndex
            component_name = temp_data[1][component_index]
            if(component_name != "" and component_name != None):
                component_data = ca.getCompData(assembly_name, component_name)
                if(component_data[1] != None):
                    return component_data[1]
                else:
                    return None
            else:
                return None
        else:
            return None

    def update_ETO_Size(self):
        self.update_list_sizes()
        self.layout.Size = self.layout.GetPreferredSize()
        self.layout.UpdateLayout()
        self.Size = drawing.Size(self.layout.Width + 20, self.layout.Height + 20)
        self.UpdateLayout()
    

    def orient_copy_components(self, assembly_name):
        print("Orienting and copying components in ", assembly_name)
        # get components from assembly and pull out one of each component by group.
        component_list = ca.getComponents(assembly_name)
        groups_to_copy = []
        for component in component_list:
            print(component)
            parts_in_component = rs.LayerChildren(component)
            print(parts_in_component)
            if(len(parts_in_component) > 0):
                temp_part = rs.ObjectsByLayer(parts_in_component[0])
                if(len(temp_part) > 0):
                    groups = rs.ObjectGroups(temp_part[0])
                    if(len(groups) > 0):
                        groups_to_copy.append([component, groups[0]])
        
        # for each group extracted, copy to DRAWINGS layer, orient objects along x-axis
        drawing_groups = []
        for group in groups_to_copy:
            new_group = rs.AddGroup()
            drawing_groups.append(new_group)
            translation_vector = rs.CreateVector(500,0,0)
            group_parts = rs.ObjectsByGroup(group[1])
            component_layer_string = group[0]
            part_layers = []
            for part in group_parts:
                part_layers.append(rs.ObjectLayer(part))
            part_layers = set(part_layers)
            part_layers = list(part_layers)
            # create layers in drawings
            for layer in part_layers:
                color = rs.LayerColor(layer)
                removed_shop = layer.replace("SHOP::", '')
                new_layer_string = "DRAWINGS::" + removed_shop
                new_part_layer = rs.AddLayer(new_layer_string, color=color)
                for part in group_parts:
                    temp_layer = rs.ObjectLayer(part)
                    if(temp_layer == layer):
                        copied_part = rs.CopyObject(part, translation_vector)
                        remove_success = rs.RemoveObjectFromGroup(copied_part, group[1])
                        rs.AddObjectToGroup(copied_part, new_group)
                        rs.ObjectLayer(copied_part, new_part_layer)
        
        num_groups = 0
        for group in drawing_groups:
            group_objects = rs.ObjectsByGroup(group)
            start_point = orient.get_rotation_axis(group_objects)
            end_point = rs.CreatePoint(1200 + num_groups * 200,0,0)
            translation_vector = rs.VectorCreate(end_point, start_point)
            rs.MoveObjects(group_objects, translation_vector)
            orient.optimize_volume(group_objects, end_point)
            num_groups = num_groups + 1



    def update_list_sizes(self):
        assembly_height = self.assembly_list.Height
        component_height = self.component_list.Height
        parts_height = self.part_list.Height
        if(assembly_height >= component_height):
            if(assembly_height >= parts_height):
                self.component_list.Size = drawing.Size(self.component_list.Width, assembly_height)
                self.part_list.Size = drawing.Size(self.part_list.Width, assembly_height)
            else:
                self.assembly_list.Size = drawing.Size(self.assembly_list.Width, self.part_list.Height)
                self.component_list.Size = drawing.Size(self.component_list.Width, self.part_list.Height)
        else:
            if(component_height >= parts_height):
                self.assembly_list.Size = drawing.Size(self.assembly_list.Width, self.component_list.Height)
                self.part_list.Size = drawing.Size(self.parts_list.Width, self.component_list.Height)
            else:
                self.assembly_list.Size = drawing.Size(self.assembly_list.Width, self.part_list.Height)
                self.component_list.Size = drawing.Size(self.component_list.Width, self.part_list.Height)


    def OnCreateAssemblyClick(self, sender, e):
        self.Visible = False
        assembly_name = self.assembly_name_textbox.Text

        # initialize layers for new assembly and default comonents
        component_name = "unsorted"
        ca.createAssembly(assembly_name)
        ca.createComponent(assembly_name, component_name)
        
        prefix = self.part_prefix_textbox.Text
        part_prefix = self.part_prefix_textbox.Text
        component_prefix = self.component_prefix_textbox.Text

        component_layer_string = "SHOP::" + assembly_name + "::" + component_name
        cp.generate_parts(part_prefix, component_prefix, assembly_name, 3.0)
        
        self.update_assembly_list()
        self.update_component_list(assembly_name)
        self.Visible = True


    def OnLayPartsFlat(self, sender, e):
        assemblies = ca.getAssemblies()
        assembly_index = self.assembly_list.SelectedIndex
        assembly_name = assemblies[assembly_index]
        lp.lay_parts_flat(assembly_name)


    def OnRemoveAssemblyClick(self, sender, e):
        print("Removing Assembly")
        assembly_index = self.assembly_list.SelectedIndex
        assemblies = ca.getAssemblies()
        assembly_name = assemblies[assembly_index]
        result = rs.MessageBox("ARE YOU SURE YOU WANT TO DELETE THE " + assembly_name + " ASSEMBLY?", 1, title="ARE YOU SURE?")
        if result == 1:
            ca.removeAssembly(assembly_name)
            self.update_assembly_list()
            # self.update_ETO_Size()

    def on_copy_orient(self, sender, e):
        assembly_index = self.assembly_list.SelectedIndex
        assemblies = ca.getAssemblies()
        assembly_name = assemblies[assembly_index]
        self.orient_copy_components(assembly_name)

    def OnRenameAssemblyClick(self, sender, e):
        print("Renaming Assembly")
    
    def OnCreateComponentClick(self, sender, e):
        print("Creating a new Component")

    def OnRemoveComponentClick(self, sender, e):
        print("Removing Component")

    def OnRenameComponentClick(self, sender, e):
        print("Renaming Component")
    
    def OnCreatePartClick(self, sender, e):
        print("Creating Part")
    
    def OnRemovePartClick(self, sender, e):
        print("Removing Part")
    
    def OnRenamePartClick(self, sender, e):
        print("Renaming Part")


    #dialog box initializer. Everythign we need to define to create the box goes here.
    def __init__(self):
        if(rs.ExeVersion() == 8):
            super().__init__()
        
        # Initalize our dialog box variables
        self.Title = 'Assembly Manager'
        self.Padding = drawing.Padding(12)
        self.Resizable = True


        # Assembly list
        self.Assembly_Box_Label = forms.Label()
        self.Assembly_Box_Label.Text = "Assemblies"
        self.assembly_list = forms.ListBox()
        self.assembly_list.DataStore =  ca.getAssemblies()
        self.assembly_list.SelectedIndex = 0
        self.assembly_list.Activated += self.update_component_list_box

        # Component List
        self.Component_Box_Label = forms.Label()
        self.Component_Box_Label.Text = "Components"
        self.component_list = forms.ListBox()
        self.component_list.DataStore = self.initialize_component_list_box()
        self.component_list.SelectedIndex = 0
        self.component_list.Activated += self.update_parts_list_box

        # Parts List
        self.Parts_Box_Label = forms.Label()
        self.Parts_Box_Label.Text = "Parts"
        self.part_list = forms.ListBox()
        self.part_list.DataStore = self.initialize_parts_list_box()
        self.part_list.SelectedIndex = 0
        self.part_list.Activated += self.update_parts_details

        # Assembly textbox and label
        self.assembly_name_label = forms.Label()
        self.assembly_name_label.Text = "Assembly Name"
        self.assembly_name_textbox = forms.TextBox()
        self.assembly_name_textbox.Text = ""
        
        # Assembly Buttons
        self.NewAssemblyButton = forms.Button()
        self.NewAssemblyButton.Text ='Create Assembly'
        self.NewAssemblyButton.Click += self.OnCreateAssemblyClick

        self.RemoveAssemblyButton = forms.Button()
        self.RemoveAssemblyButton.Text ='Remove Assembly'
        self.RemoveAssemblyButton.Click += self.OnRemoveAssemblyClick

        self.RenameAssemblyButton = forms.Button()
        self.RenameAssemblyButton.Text ='Rename Assembly'
        self.RenameAssemblyButton.Click += self.OnRenameAssemblyClick
        
        # Component textbox and label
        self.component_name_label = forms.Label()
        self.component_name_label.Text = "Component Name"
        self.component_name_textbox = forms.TextBox()
        self.component_name_textbox.Text = ""

        self.component_quantity_label = forms.Label()
        self.component_quantity_label.Text = "Component Quantity"
        self.component_quantity_textbox = forms.TextBox()
        self.component_quantity_textbox.Text = ""

        # Component Buttons
        self.NewComponentButton = forms.Button()
        self.NewComponentButton.Text ='Create Component'
        self.NewComponentButton.Click += self.OnCreateComponentClick
        # self.NewComponentButton.Size = drawing.Size(5,5)

        self.RemoveComponentButton = forms.Button()
        self.RemoveComponentButton.Text ='Remove Component'
        self.RemoveComponentButton.Click += self.OnRemoveComponentClick
        # self.RemoveComponentButton.Size = drawing.Size(5,5)

        self.RenameComponentButton = forms.Button()
        self.RenameComponentButton.Text ='Rename Component'
        self.RenameComponentButton.Click += self.OnRenameComponentClick
        # self.RenameComponentButton.Size = drawing.Size(5,5)

        self.lay_parts_flat_button = forms.Button()
        self.lay_parts_flat_button.Text ='Lay Parts Flat'
        self.lay_parts_flat_button.Click += self.OnLayPartsFlat

        self.copy_orient_components_button = forms.Button()
        self.copy_orient_components_button.Text = "Copy / Orient Components"
        self.copy_orient_components_button.Click += self.on_copy_orient

        # Part textbox and label
        self.part_name_label = forms.Label()
        self.part_name_label.Text = "Part Name"
        self.part_name_textbox = forms.TextBox()
        self.part_name_textbox.Text = ""

        self.part_prefix_label = forms.Label()
        self.part_prefix_label.Text = "Part Prefix"
        self.part_prefix_textbox = forms.TextBox()
        self.part_name_textbox.Text = ""

        self.component_prefix_label = forms.Label()
        self.component_prefix_label.Text = "Component Prefix"
        self.component_prefix_textbox = forms.TextBox()
        self.component_prefix_textbox.Text = ""

        # Component Buttons
        self.NewPartButton = forms.Button()
        self.NewPartButton.Text ='Create Part'
        self.NewPartButton.Click += self.OnCreatePartClick
        # self.NewComponentButton.Size = drawing.Size(5,5)

        self.RemovePartButton = forms.Button()
        self.RemovePartButton.Text ='Remove Part'
        self.RemovePartButton.Click += self.OnRemovePartClick
        # self.RemoveComponentButton.Size = drawing.Size(5,5)

        self.RenamePartButton = forms.Button()
        self.RenamePartButton.Text ='Rename Part'
        self.RenamePartButton.Click += self.OnRenamePartClick
        # self.RenameComponentButton.Size = drawing.Size(5,5)

        

        # Create a table layout and add controls to the table
        self.layout = forms.DynamicLayout()
        self.layout.Spacing = drawing.Size(10,10)
        self.layout.Padding = drawing.Padding(10)

        self.layout.BeginHorizontal()
        
        self.layout.BeginVertical(spacing=drawing.Size(10,3))
        self.layout.AddRow(self.Assembly_Box_Label)
        self.layout.AddRow(self.assembly_list)
        self.layout.AddRow(self.part_prefix_label, self.part_prefix_textbox)
        self.layout.AddRow(self.assembly_name_label, self.assembly_name_textbox)
        self.layout.AddRow(self.component_prefix_label, self.component_prefix_textbox)
        self.layout.AddRow(None)
        self.layout.AddRow(self.NewAssemblyButton, self.RemoveAssemblyButton)
        self.layout.EndVertical()

        self.layout.BeginVertical(spacing=drawing.Size(10, 3))
        self.layout.AddRow(self.Component_Box_Label)
        self.layout.AddRow(None)
        self.layout.AddRow(self.component_list)
        self.layout.AddRow(self.component_name_label, self.component_name_textbox)
        self.layout.AddRow(self.component_quantity_label, self.component_quantity_textbox)
        self.layout.AddRow(None)
        self.layout.AddRow(self.lay_parts_flat_button)
        self.layout.AddRow(self.copy_orient_components_button)
        self.layout.EndVertical()
        
        self.layout.BeginVertical(spacing=drawing.Size(10, 3))
        self.layout.AddRow(self.Parts_Box_Label)
        self.layout.AddRow(self.part_list)
        self.layout.AddRow(self.part_name_label, self.part_name_textbox)

        self.layout.EndVertical()
        self.layout.EndHorizontal()

        # set the dialog content to the layout you created
        self.Content = self.layout


        ## End dialog initializer class ##


# The script that will use that dialog class we just defined
def RunDialog():
    dialog = AssemblyManagerEtoDialog()
    rc = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)
    if(rc):
        print("empty")

# Boilerplate for main
if __name__ == "__main__":
	RunDialog()