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
#! python 3

import Eto.Drawing as drawing
import Eto.Forms as forms
import rhinoscriptsyntax as rs
import scriptcontext as sc
import math

import System
import System.Collections.Generic
import Rhino

import Layout_Manager as lm
import Create_Assembly as ca
import Orient as orient


width_buffer = 0.125
height = 0.5
vert_line_offset = 0.25
vert_text_padding = 0.03
horizontal_text_padding = 0.05
horiz_text_padding = 0.024
text_height = 0.125
scale_text_height = 0.080
circle_radius = 0.144
center_line_length = 2.03125


view_type_enum = [
    'Top',
    'Bottom',
    'Left',
    'Right',
    'Front',
    'Back',
    'Perspective'
]

detail_level_enum = [
    'Assembly',
    'Component',
    'Part'
]

def save_detail(detail_ID):
    print("Saving detail parameters to ")


def get_point_user():
    pt = rs.GetPoint("Pick Start Point")
    return pt


def get_user_window():
    rect = rs.GetRectangle(mode=1)
    return rect


def create_detail():
    print("Creating Detail")
    # Load in ETO window parameters


def update_details():
    print("updating details") 
    # for each detail, account for what details are in what layout, then re-number them. 

def deconstruct_detail_string(data):
    # info that needs stored... 
    # Which layout it belongs to 
    # What component, assembly or part it is referencing
    print(data)
    return data

def construct_detail_string(data):
    data_string = data
    return data

def get_distance(pt1, pt2):
    x = rs.coerce3dpoint(pt2)[0] - rs.coerce3dpoint(pt1)[0]
    y = rs.coerce3dpoint(pt2)[1] - rs.coerce3dpoint(pt1)[1]
    z = rs.coerce3dpoint(pt2)[2] - rs.coerce3dpoint(pt1)[2]
    return  (x*x + y*y + z*z)**0.5


# Deprecated
def create_label(corner_point, number, label, scale, text_height):

    num_pt = rs.CopyObject(corner_point)
    rs.MoveObject(num_pt, rs.CreateVector(vert_line_offset / 2, text_height / -2.0, 0))
    num_text = rs.AddText(str(number), num_pt, height=text_height, justification=2)
    
    label_pt = rs.CopyObject(corner_point)
    rs.MoveObject(label_pt, rs.CreateVector(vert_line_offset + horizontal_text_padding, text_height / -2.0, 0))
    label_text = rs.AddText(label, label_pt, height=text_height, justification=1)
    box = rs.BoundingBox(label_text)
    label_width = get_distance(box[0], box[1])

    
    scale_pt = rs.CopyObject(corner_point)
    rs.MoveObject(scale_pt, rs.CreateVector(vert_line_offset + horizontal_text_padding, height / -2.0 - (text_height / 2.0), 0))
    scale_string = "Scale : " + scale
    scale_text = rs.AddText(scale_string, scale_pt, height=text_height, justification=1)
    scale_box = rs.BoundingBox(scale_text)
    scale_width = get_distance(scale_box[0], scale_box[1])

    v_pt1 = rs.CopyObject(corner_point)
    rs.MoveObject(v_pt1, rs.CreateVector(vert_line_offset, 0,0))
    v_pt2 = rs.CopyObject(corner_point)
    rs.MoveObject(v_pt2, rs.CreateVector(vert_line_offset, height * -1.0, 0))
    vertical_line = rs.AddLine(v_pt1, v_pt2)

    h_pt1 = rs.CopyObject(corner_point)
    rs.MoveObject(h_pt1, rs.CreateVector(0, height / -2.0, 0))
    h_pt2 = rs.CopyObject(corner_point)
    if(label_width > scale_width):
        rs.MoveObject(h_pt2, rs.CreateVector(label_width + width_buffer + vert_line_offset + horizontal_text_padding, height / -2.0, 0))
    else:
        rs.MoveObject(h_pt2, rs.CreateVector(scale_width + width_buffer + vert_line_offset + horizontal_text_padding, height / -2.0, 0))
    
    horizontal_line = rs.AddLine(h_pt1, h_pt2)
    rs.DeleteObjects([v_pt1, v_pt2, h_pt1, h_pt2, scale_pt, label_pt, num_pt])
    return [scale_text, label_text, num_text, vertical_line, horizontal_line]


# create mark's label
def create_label_2(corner_point, number, label, scale, text_height):

    starting_point = rs.CopyObject(corner_point)
    num_circle_point = rs.CopyObject(corner_point)
    num_pt = rs.CopyObject(corner_point)
    rs.MoveObject(num_pt, rs.CreateVector(vert_line_offset / 2, text_height / -2.0, 0))
    num_text = rs.AddText(str(number), num_pt, height=text_height, justification=2)

    rs.MoveObject(num_circle_point, rs.CreateVector(vert_line_offset / 2, text_height / -1.0, 0))
    num_circle = rs.AddCircle(num_circle_point, circle_radius)
    center_line_start_point = rs.CopyObject(num_circle_point)
    rs.MoveObject(center_line_start_point, rs.CreateVector(circle_radius, 0,0))
    center_line_end_point = rs.CopyObject(center_line_start_point)
    rs.MoveObject(center_line_end_point, rs.CreateVector(center_line_length, 0, 0))

    center_line = rs.AddLine(center_line_start_point, center_line_end_point)
    text_horiz_padding_point = rs.CopyObject(center_line_start_point)
    rs.MoveObject(text_horiz_padding_point, rs.CreateVector(horiz_text_padding, 0,0))
    label_text_point = rs.CopyObject(text_horiz_padding_point)
    scale_text_point = rs.CopyObject(text_horiz_padding_point)

    rs.MoveObject(label_text_point, rs.CreateVector(0, text_height + vert_text_padding, 0))
    rs.MoveObject(scale_text_point, rs.CreateVector(0, -1.0 * vert_text_padding, 0))

    scale_text = rs.AddText("SCALE: " + str(scale), scale_text_point, height = scale_text_height, justification=1)
    label_text = rs.AddText(str(label), label_text_point, height=text_height, justification=1)
    rs.DeleteObjects([
        label_text_point, 
        scale_text_point, 
        text_horiz_padding_point, 
        starting_point, 
        num_circle_point, 
        num_pt, 
        center_line_start_point, 
        center_line_end_point
    ])

    return [scale_text, label_text, num_text, center_line]




# get's the number the detail should be
def get_detail_number():
    detail_list = rs.GetDocumentData("Details")
    return 1

# Collects all the parts on an assembly layer and returns them as a list of GUIDs
def get_assembly_objects(layer_string):
    objects = []
    components = rs.LayerChildren(layer_string)
    for component in components:
        parts = rs.LayerChildren(component)
        for part in parts:
            temp_parts = rs.ObjectsByLayer(part)
            objects = objects + temp_parts
    return objects


# gets the bounding box of a list of objects and returns a scaled set of points
def get_bounding_scaled(objects, scale_factor):
    bounding_points = rs.BoundingBox(objects)
    center_point = orient.average_points(bounding_points)
    scaled_points = rs.ScaleObjects(bounding_points, center_point, [scale_factor, scale_factor, scale_factor])
    return scaled_points

# ETO dialog for the NewDetail
class NewDetailEto(forms.Dialog[bool]):

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


    def OnQuadButton(self, sender, e):
        detail_width = 8.375
        detail_height = 5.0
        start_x = 0.125
        start_y = 10.875

        label_vert_offset = 0.3
        label_horiz_offset = 0.125


        perp_front_start = rs.CreatePoint([start_x, start_y])
        perp_front_end = rs.CreatePoint([start_x + detail_width, start_y - detail_height])
        perp_label_corner = rs.CreatePoint([start_x + label_horiz_offset, start_y - detail_height + label_vert_offset])

        plan_start = rs.CreatePoint([start_x + detail_width, start_y])
        plan_end = rs.CreatePoint([start_x + detail_width * 2.0, start_y - detail_height])
        plan_label_corner = rs.CreatePoint([start_x + detail_width + label_horiz_offset, start_y - detail_height + label_vert_offset])

        front_start = rs.CreatePoint([start_x, start_y - detail_height])
        front_end = rs.CreatePoint([start_x + detail_width, start_y - detail_height * 2.0])
        front_label_corner = rs.CreatePoint([start_x + label_horiz_offset, start_y - detail_height * 2.0 + label_vert_offset])

        left_start = rs.CreatePoint([start_x + detail_width, start_y - detail_height])
        left_end = rs.CreatePoint([start_x + detail_width * 2.0, start_y - detail_height * 2.0])
        left_label_corner = rs.CreatePoint([start_x + detail_width + label_horiz_offset, start_y - detail_height * 2.0 + label_vert_offset])


        layouts = lm.populateLayoutList()
        layout_name = layouts[self.layout_dropdown.SelectedIndex]
        view_names = rs.ViewNames(return_names=True, view_type=1)
        view_index = view_names.index(layout_name)
        view_ids = rs.ViewNames(return_names=False, view_type=1)
        current_view_id = view_ids[view_index]
        
        perp_detail_id = rs.AddDetail(current_view_id, perp_front_start, perp_front_end, title="Perspective View", projection=7)
        plan_detail_id = rs.AddDetail(current_view_id, plan_start, plan_end, title="Plan View", projection=1)
        front_detail_id = rs.AddDetail(current_view_id, front_start, front_end, title="Front Elevation", projection=5)
        left_detail_id = rs.AddDetail(current_view_id, left_start, left_end, title="Left Elevation", projection=3)

        perp_label = create_label_2(perp_label_corner, 1, "Perspective View", "NTS", text_height)
        plan_label = create_label_2(plan_label_corner, 2, "Plan View", "NTS", text_height)
        front_label = create_label_2(front_label_corner, 3, "Front Elevation", "NTS", text_height)
        left_label = create_label_2(left_label_corner, 4, "Left Elevation", "NTS", text_height)

        self.set_view_to_object(perp_detail_id)
        self.set_view_to_object(plan_detail_id)
        self.set_view_to_object(front_detail_id)
        self.set_view_to_object(left_detail_id)


        

    

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


        self.quad_detail_button = forms.Button()
        self.quad_detail_button.Text = "Create Quad"
        self.quad_detail_button.Click += self.OnQuadButton

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
        layout.AddRow(self.quad_detail_button)

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
