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

import Eto.Drawing as drawing
import Eto.Forms as forms


# Sets up four details in the quads of the window based on an assembly or component we want to center around. 

# ETO dialog for the NewDetail
class QuadDetailDialog(forms.Dialog[bool]):

    # Centers view of a detail around object. This currently only works on an assembly level. 
    def set_view_to_object(self, detail_id):
        if(self.detail_level_dropdown.SelectedIndex == 0):
            # assembly level
            assemblies = self.load_assemblies()
            assembly_name = assemblies[self.object_selection_dropdown.SelectedIndex]
            layouts = lm.populateLayoutList()
            layout_name = layouts[self.layout_dropdown.SelectedIndex]
            rs.CurrentDetail(layout_name, detail=detail_id)
            assembly_objects = get_assembly_objects("SHOP::" + assembly_name)
            bounding_points = get_bounding_scaled(assembly_objects, 1.25)
            rs.ZoomBoundingBox(bounding_points, view=rs.ViewTitle(detail_id))
            rs.DeleteObjects(bounding_points)
            rs.ViewDisplayMode(view=rs.ViewTitle(detail_id), mode="Shaded")

            page_views = sc.doc.Views.GetPageViews()
            print(page_views)
            for page_view in page_views:
                detail_views = Rhino.Display.RhinoPageView.GetDetailViews(page_view)
                print(len(detail_views))
                for detail_view in detail_views:
                    print(detail_view)
                    if(detail_view.IsActive):
                        detail_view.IsActive = False
        

        elif(self.detail_level_dropdown.SelectedIndex == 1):
            # component level
            print("component level")
        elif(self.detail_level_dropdown.SelectedIndex == 2):
            # part level
            print("part level")

    # retrieves the information from document data
    def retrieve_detail(detail_name):
        detail_string = rs.GetDocumentData("Details", detail_name)
        detail_data = deconstruct_detail_string(detail_string)
        print(detail_data)
        return detail_data

    # saves the detail object to the document data as a string
    def save_detail(self, detail_name, layout_name):
        detail_string = construct_detail_string(layout_name)
        rs.SetDocumentData("Details", detail_name, detail_string)
        print("Storing detail in document data")
    
    # loads the list of details on startup of the ETO window
    # returns array for list box
    def initialize_detail_list(self):
        print("loading list box with details")

    # initializes the document data for where details should exist as a list
    def initialize_detail_storage(self):
        print("If document doesn't yet have parameter for this, initialize it")
    
    
    def switch_layout_focus(self):
        # makes sure the layout selected in the dropdown is the current layout in window.
        print("")
    
    def get_current_layout(self):
        print("finding out if a layout is already the primary focus of the users window. If yes, return index.")


    def OnCreateButton(self, sender, e):
        print("Creating a new Detail")
        self.Visible = False
        rect = get_user_window()
        self.Visible = True
        layouts = lm.populateLayoutList()
        layout_name = layouts[self.layout_dropdown.SelectedIndex]
        view_names = rs.ViewNames(return_names=True, view_type=1)
        view_index = view_names.index(layout_name)
        view_ids = rs.ViewNames(return_names=False, view_type=1)
        current_view_id = view_ids[view_index]
        detail_id = rs.AddDetail(current_view_id, rect[0], rect[2], title=self.detail_textbox.Text, projection=self.view_dropdown.SelectedIndex + 1)
        detail_number = get_detail_number()
        matrix = rs.XformTranslation((0,-0.125,0))
        label_corner = rs.PointTransform(rect[0], matrix)
        [scale_text, label_text, num_text, horizontal_line] = create_label_2(label_corner, detail_number, self.detail_textbox.Text, "NTS", text_height)
        self.set_view_to_object(detail_id)
    

    def load_objects(self, sender, e):
        print(self.detail_level_dropdown.SelectedIndex)
        if(self.detail_level_dropdown.SelectedIndex == 0):
            self.object_selection_dropdown.DataStore = self.load_assemblies()
            self.object_selection_dropdown.SelectedIndex = 0
        elif(self.detail_level_dropdown.SelectedIndex == 1):
            self.object_selection_dropdown.DataStore = self.load_components()
            self.object_selection_dropdown.SelectedIndex = 0
        elif(self.detail_level_dropdown.SelectedIndex == 2):
            self.object_selection_dropdown.DataStore = self.load_parts()
            self.object_selection_dropdown.SelectedIndex = 0
    

    def init_objects(self):
        if(self.detail_level_dropdown.SelectedIndex == 0):
            return self.load_assemblies()
        elif(self.detail_level_dropdown.SelectedIndex == 1):
            return self.load_components()
        elif(self.detail_level_dropdown.SelectedIndex == 2):
            return self.load_parts()

    # loads a list of all components in all assemblies
    def load_components(self):
        assemblies = ca.getAssemblies()
        component_list = []
        for assembly in assemblies:
            components = ca.getComponents(assembly)
            for component in components:
                component_list.append(component)
        return component_list

    # returns a list of all assemblies
    def load_assemblies(self):
        print("reloading assemblies")
        return ca.getAssemblies()

    # loads a list of all parts
    def load_parts(self):
        print("reloading parts")
        return ["nothing"]
    
    
    def OnCancelButton(self, sender, e):
        print("Closing Create Detail Window")
        self.Close(False)


    def __init__(self):
        if(rs.ExeVersion() == 8):
            super().__init__()

        # Form settings
        self.Title = 'Create New Detail'
        self.Padding = drawing.Padding(10)
        self.Resizable = True

        # Form elements
        self.detail_label = forms.Label()
        self.detail_label.Text = "Detail Name : "
        self.detail_textbox = forms.TextBox()
        self.detail_textbox.Text = ""

        self.view_dropdown_label = forms.Label()
        self.view_dropdown_label.Text = "View Type : "
        self.view_dropdown = forms.DropDown()
        self.view_dropdown.DataStore = view_type_enum
        self.view_dropdown.SelectedIndex = 0

        self.object_selection_label = forms.Label()
        self.object_selection_label.Text = "Object : "
        self.object_selection_dropdown = forms.DropDown()
        self.object_selection_dropdown.SelectedIndex = 0

        self.detail_level_dropdown_label = forms.Label()
        self.detail_level_dropdown_label.Text = "Detail Level : "
        self.detail_level_dropdown = forms.DropDown()
        self.detail_level_dropdown.DataStore = detail_level_enum
        self.detail_level_dropdown.SelectedIndex = 0

        self.detail_level_dropdown.SelectedIndexChanged += self.load_objects
        self.object_selection_dropdown.DataStore = self.init_objects()

        self.layout_dropdown_label = forms.Label()
        self.layout_dropdown_label.Text = "Layout : "
        self.layout_dropdown = forms.DropDown()
        self.layout_dropdown.DataStore = lm.populateLayoutList()
        self.layout_dropdown.SelectedIndex = 0


        self.new_detail_button = forms.Button()
        self.new_detail_button.Text = "Create"
        self.new_detail_button.Click += self.OnCreateButton

        self.cancel_button = forms.Button()
        self.cancel_button.Text = "Cancel"
        self.cancel_button.Click += self.OnCancelButton


        # table layout
        layout = forms.DynamicLayout()
        layout.Spacing = drawing.Size(5,5)
        layout.AddRow(self.detail_label, self.detail_textbox)
        layout.AddRow(self.view_dropdown_label, self.view_dropdown)
        layout.AddRow(self.layout_dropdown_label, self.layout_dropdown)
        layout.AddRow(self.detail_level_dropdown_label, self.detail_level_dropdown)
        layout.AddRow(self.object_selection_label, self.object_selection_dropdown)
        layout.AddRow(None)
        layout.AddRow(self.new_detail_button, self.cancel_button)

        self.Content = layout


def run_window():
    dialog = NewDetailEto()
    rc = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)
    if(rc):
        print("print")

if __name__ == "__main__":
    # corner_point = get_point_user()
    # create_label(corner_point, "1", "Perspective View (Context)", "NTS", text_height)
    run_window()
