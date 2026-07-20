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

import Orient as orient

# gets a user selection for a detail object. 
def get_detail():
    detail_object = rs.GetObject(message="Select a detail you would like to label", filter=32768, preselect=True)
    return detail_object

# UI for pop-up list menu in the Rhino interface
def get_label_level():
    menu_items = ["Assembly", "Component"]
    label_level = rs.PopupMenu(items=menu_items)
    return label_level

# Gets all parts in either the drawings or shops parent layer
def get_all_drawing_objects(label_level):
    if(label_level == 1):
        drawing_assemblies = rs.LayerChildren("DRAWINGS")
    else:
        drawing_assemblies = rs.LayerChildren("SHOP")
    
    drawing_components = []
    for assembly in drawing_assemblies:
        temp_components = rs.LayerChildren(assembly)
        for component in temp_components:
            drawing_components.append(component)
    
    drawing_part_layers = []
    for component in drawing_components:
        temp_part_layers = rs.LayerChildren(component)
        for part_layer in temp_part_layers:
            drawing_part_layers.append(part_layer)
    
    all_drawing_objects = []
    for part_layer in drawing_part_layers:
        objects_on_layer = rs.ObjectsByLayer(part_layer)
        for obj in objects_on_layer:
            all_drawing_objects.append(obj)
    # print(all_drawing_objects)
    return all_drawing_objects


# discerns whether an object is visible in a detail view and returns only the list of visible objects. 
def get_visible_objects(obj_guids, view_id):
    detail_object = None
    detail_views = []
    page_view = sc.doc.Views.GetPageViews()
    for view in page_view:
        temp_details = Rhino.Display.RhinoPageView.GetDetailViews(view)
        for detail in temp_details:
            detail_views.append(detail)
    
    for view in detail_views:
        if(view.Id == view_id):
            detail_object = view
            break
    
    if(detail_object != None):
        detail_viewport = detail_object.Viewport
    else:
        return [None, None]

    visible_objects = []
    for obj in obj_guids:
        bb = rs.BoundingBox(obj)
        box = Rhino.Geometry.BoundingBox(bb)
        visible = detail_viewport.IsVisible(box)
        if(visible):
            visible_objects.append(obj)
    return [visible_objects, detail_object]


# applies text dots to current layout space
def apply_dots(objects, detail_object, label_level):
    world_to_page_xform = detail_object.WorldToPageTransform
    if(label_level == 1):
        # this is a Component level labeling (labels parts)
        for obj in objects:
            centroid = rs.SurfaceVolumeCentroid(obj)
            page_point = rs.TransformObject(centroid[0], world_to_page_xform)
            part_name = get_object_layer_child(obj)
            rs.AddTextDot(part_name, rs.coerce3dpoint(page_point))
            rs.DeleteObject(page_point)
    else:
        # else this is an assembly level labeling (labels components)
        # extract all groups from the list
        group_list = []
        for obj in objects:
            temp_groups = rs.ObjectGroups(obj)
            for temp_group in temp_groups:
                if(temp_group not in group_list):
                    group_list.append(temp_group)
        
        for group in group_list:        
            grouped_objects = rs.ObjectsByGroup(group)
            bounding_box_points = rs.BoundingBox(grouped_objects)
            bounding_box_average = orient.average_points(bounding_box_points)
            full_layer_string = rs.ObjectLayer(grouped_objects[0])
            component_name = strip_layer_to_component(full_layer_string)
            page_point = rs.TransformObject(bounding_box_average, world_to_page_xform)
            rs.AddTextDot(component_name, page_point)
            rs.DeleteObject(page_point)


def strip_layer_to_component(part_layer_string):
    layer_split_list = part_layer_string.split("::")
    return layer_split_list[len(layer_split_list) - 2]

# strips the lowest layer name off of a full layer string and returns as string
def get_object_layer_child(obj):
    full_layer = rs.ObjectLayer(obj)
    part_name = strip_layer_to_child([full_layer])
    return part_name[0]

# strips the lowest layer name off a list of full layer strings, returns stripped list
def strip_layer_to_child(layer_list):
    stripped_list = []
    for layer in layer_list:
        layer_split_list = layer.split("::")
        stripped_list.append(layer_split_list[-1])
    return stripped_list

# top level function commands. 
def runner():
    detail_id = get_detail()
    label = get_label_level()
    temp_detail_name = "a"
    all_drawing_objects = get_all_drawing_objects(label)
    [visible_objects, detail_object] = get_visible_objects(all_drawing_objects, detail_id)
    apply_dots(visible_objects, detail_object, label)


if __name__ == "__main__":
    runner()
