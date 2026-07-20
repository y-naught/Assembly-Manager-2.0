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
import copy

import System
import System.Collections.Generic
import Rhino



def construct_shop_string(assembly_name, component_name):
    layer_string = "SHOP::" + assembly_name + "::" + component_name
    return layer_string

def construct_cam_string(assembly_name, component_name, part_name):
    layer_string = "CAM::" + assembly_name + "::" + component_name + "::" + part_name
    return layer_string

def extract_part_label(layer):
    layer_hierarchy = layer.split("::")
    part_label = layer_hierarchy[len(layer_hierarchy) - 1]
    print(part_label)
    return part_label

def extract_parts(assembly_name):
    # get all part labels in a component
    components = rs.LayerChildren(assembly_name)
    all_parts = []
    parts_set = set()
    for component in components:
        component_layer_string = "SHOP::" + assembly_name + "::" + component
        part_layers = rs.LayerChildren(component)
        all_parts.append(part_layers)
        print(part_layers)
        for single_part in part_layers:
            parts_set.add(extract_part_label(single_part))

    all_parts = list(parts_set)
    all_parts.sort()
    unique_parts = []
    for part in all_parts:
        # find a component with the part on it
        part_found = False
        for component in components:
            parts_in_component = rs.LayerChildren(component)
            stripped_parts = []
            for part_val in parts_in_component:
                stripped_parts.append(extract_part_label(part_val))
            print("stripped_parts")
            print(stripped_parts)
            if(part in stripped_parts and not part_found):
                part_found = True
                full_part_string = component + "::" + part
                part_layer_id = rs.LayerId(full_part_string)
                parts = rs.ObjectsByLayer(part_layer_id)
                unique_parts.append(parts[0])
    return unique_parts


def get_part_counts(assembly_name):

    components = rs.LayerChildren(assembly_name)
    all_parts = []
    parts_set = set()
    for component in components:
        component_layer_string = "SHOP::" + assembly_name + "::" + component
        part_layers = rs.LayerChildren(component)
        all_parts.append(part_layers)
        for single_part in part_layers:
            parts_set.add(extract_part_label(single_part))
    
    all_parts = list(parts_set)
    all_parts.sort()
    part_counts = []
    for part in all_parts:
        entry = {
            "name" : extract_part_label(part),
            "count" : 0
        }
        part_counts.append(entry)
    
    for part in part_counts:
        for component in components:
            component_part_layers = rs.LayerChildren(component)
            for component_part in component_part_layers:
                full_part_string = component_part
                part_layer_id = rs.LayerId(full_part_string)
                parts_on_layer = rs.ObjectsByLayer(part_layer_id)
                if(part["name"] == extract_part_label(component_part)):
                    part["count"] += len(parts_on_layer)
        
    return part_counts


def create_CAM_layers(assembly_name):
    # create layers
    new_layer_string = "CAM::" + assembly_name
    print(new_layer_string)
    if(rs.IsLayer(new_layer_string)):
        print("Layer set exists")
    else:
        print("createing layer : ", new_layer_string)
        rs.AddLayer(new_layer_string)


def assign_moved_parts_to_CAM_layers(parts, assembly_name):
    for part in parts:
        current_layer = rs.ObjectLayer(part)
        layer_color = rs.LayerColor(current_layer)
        part_label = extract_part_label(current_layer)
        layer_string = "CAM::" + assembly_name + "::" + part_label + "::3D"
        new_layer = rs.AddLayer(layer_string)
        rs.LayerColor(new_layer, layer_color)
        rs.ObjectLayer(part, new_layer)


def generate_text(assembly_name, counts, materials, anchor_points):
    move_distance = -18
    text_height = 0.125
    moved_points = []
    for point in anchor_points:
        xform = rs.XformTranslation([0,move_distance,0])
        moved_point = rs.TransformObject(point, xform)
        moved_points.append(moved_point)
    text_objects = []
    for i in range(len(counts)):
        material = str(round(materials[i], 3)) + "\""
        temp_text_object = add_text_object(assembly_name, counts[i]["name"], material, counts[i]["count"], moved_points[i], text_height)
        text_objects.append(temp_text_object)
    rs.DeleteObjects(moved_points)
    return text_objects


def add_text_object(assembly_name, part_label, material, num_parts, anchor_point, text_height):
    print("Creating text object for ", part_label)
    cam_layer = "CAM::" + assembly_name + "::" + part_label
    text_layer_string = cam_layer + "::text"
    text_layer = rs.AddLayer(text_layer_string)
    text = part_label + "\n" + "QTY : " + str(num_parts) + "\n" + material
    text_object = rs.AddText(text, anchor_point, height=text_height)
    rs.ObjectLayer(text_object, text_layer)
    return text_object


# circumvents the distance function issue
def get_distance(pt1, pt2):
    x = rs.coerce3dpoint(pt2)[0] - rs.coerce3dpoint(pt1)[0]
    y = rs.coerce3dpoint(pt2)[1] - rs.coerce3dpoint(pt1)[1]
    z = rs.coerce3dpoint(pt2)[2] - rs.coerce3dpoint(pt1)[2]
    return  (x*x + y*y + z*z)**0.5

def get_max_index(areas):
    record = 0
    index = -1
    for i in range(len(areas)):
        if(areas[i] > record):
            index = i
            record = areas[i]
    return index
            
# returns the a surface frame based on the largest object
def get_normal_largest_surface(part):
    all_surfaces = rs.ExplodePolysurfaces(part)
    areas = []
    for surface in all_surfaces:
        areas.append(rs.SurfaceArea(surface)[0])
    max_index = get_max_index(areas)
    n_frame = rs.SurfaceFrame(all_surfaces[max_index],[0,0])
    # normal_vector = rs.SurfaceNormal(all_surfaces[max_index],[0,0])
    # surface_point = rs.EvaluateSurface(all_surfaces[max_index], [0,0])
    # return [normal_vector, surface_point]
    max_surface = rs.CopyObject(all_surfaces[max_index])
    rs.DeleteObjects(all_surfaces)
    return [n_frame, max_surface]

def get_direction_thickness(part, normal_plane):
    # create bounding box oriented to part surface
    bounding_box = rs.BoundingBox(part, normal_plane)
    dims = []
    if(bounding_box):
        tempX = get_distance(bounding_box[1], bounding_box[0])
        tempY = get_distance(bounding_box[3] , bounding_box[0])
        tempZ = get_distance(bounding_box[4], bounding_box[0])
        dims.extend([tempX, tempY, tempZ])
    # max index seems to return 0 for X longest, 1 for z longest, and I would assume 2 for y longest
    max_index = get_max_index(dims)
    return([dims, max_index])
    

def get_points_from_plane(normal_frame):
    domainU = rs.SurfaceDomain(normal_frame, 0)
    domainV = rs.SurfaceDomain(normal_frame, 1)
    u = domainU[0]
    print(domainU)
    v = domainV[0]
    print(domainV)

    mid_u = (domainU[1] - domainU[0]) / 2.0
    mid_v = (domainV[1] - domainV[0]) / 2.0

    origin = None
    x_axis = None
    y_axis = None
 
    if(mid_u >= 0 and mid_v >= 0):
        origin = rs.EvaluateSurface(normal_frame, u, v)
        x_axis = rs.EvaluateSurface(normal_frame, u+1.0, v)
        y_axis = rs.EvaluateSurface(normal_frame, u, v+1.0)
    elif(mid_u < 0 and mid_v >= 0):
        origin = rs.EvaluateSurface(normal_frame, u, v)
        x_axis = rs.EvaluateSurface(normal_frame, u-1.0, v)
        y_axis = rs.EvaluateSurface(normal_frame, u, v+1.0)
    elif(mid_u >= 0 and mid_v < 0):
        origin = rs.EvaluateSurface(normal_frame, u, v)
        x_axis = rs.EvaluateSurface(normal_frame, u+1.0, v)
        y_axis = rs.EvaluateSurface(normal_frame, u, v-1.0)
    else:
        origin = rs.EvaluateSurface(normal_frame, u, v)
        x_axis = rs.EvaluateSurface(normal_frame, u-1.0, v)
        y_axis = rs.EvaluateSurface(normal_frame, u, v-1.0)
    
    return([origin, x_axis, y_axis])


def create_transformation(start_points, normal_vec, end_points, direction_integer):
    unitz = rs.CreateVector(0,0,1)
    x0 = (rs.VectorCreate(start_points[1], start_points[0]))
    y0 = (rs.VectorCreate(start_points[2], start_points[0]))
    z0 = (normal_vec)
    x1 = (rs.VectorCreate(end_points[1], end_points[0]))
    y1 = (rs.VectorCreate(end_points[2], end_points[0]))
    z1 = unitz
    return rs.XformRotation4(x0, y0, z0, x1, y1, z1)

# Grabs each unique part based on their layer, lays them flat (if planar)
# returns the moved parts
def isolate_orient_parts(unique_parts):
    # generate the frames normal to the largest surface
    normal_frames = []
    normal_surfaces = []
    for part in unique_parts:
        n_frame = get_normal_largest_surface(part)
        normal_frames.append(n_frame[0])
        normal_surfaces.append(n_frame[1])
    
    # get points from those frames
    frame_points = []
    for frame in normal_surfaces:
        points = get_points_from_plane(frame)
        print(points)
        frame_points.append(points)
    
    # generate transformations
    anchor_points = []
    padding = 60
    start_point = rs.CreatePoint(200,0,0)
    anchor_points.append(start_point)
    xform_planes = []
    parts = []
    min_dims = []
    new_anchors = []
    anchor_guids = []
    for i in range(len(unique_parts)):
        dims_index = get_direction_thickness(unique_parts[i], normal_frames[i])
        material_thickness = round(min(dims_index[0]), 3)
        sorted_dims = copy.deepcopy(dims_index[0])
        sorted_dims.sort()
        mid_dimension = sorted_dims[1]
        min_dimension = sorted_dims[0]
        translation_vector = rs.CreateVector(mid_dimension + padding, 0, 0)
        next_anchor = rs.CopyObject(anchor_points[-1], translation_vector)
        anchor_guids.append(str(next_anchor))
        anchor_points.append(next_anchor)
        next_x = rs.CopyObject(next_anchor, rs.CreateVector(1,0,0))
        next_y = rs.CopyObject(next_anchor, rs.CreateVector(0,1,0))
        main_translate = rs.VectorCreate(next_anchor, frame_points[i][0])

        new_part = rs.CopyObject(unique_parts[i])


        if(rs.ExeVersion() == 8):
            new_plane = Rhino.Geometry.Plane.CreateFromPoints(rs.coerce3dpoint(next_anchor), rs.coerce3dpoint(next_x), rs.coerce3dpoint(next_y))
            normal_plane = Rhino.Geometry.Plane.CreateFromPoints(rs.coerce3dpoint(frame_points[i][0]), rs.coerce3dpoint(frame_points[i][2]), rs.coerce3dpoint(frame_points[i][1]))
        else:
            new_plane = Rhino.Geometry.Plane(rs.coerce3dpoint(next_anchor), rs.coerce3dpoint(next_x), rs.coerce3dpoint(next_y))
            normal_plane = Rhino.Geometry.Plane(rs.coerce3dpoint(frame_points[i][0]), rs.coerce3dpoint(frame_points[i][2]), rs.coerce3dpoint(frame_points[i][1]))

        transformation_planes = rs.XformRotation1(normal_plane, new_plane)


        #print(transformation)
        xform_planes.append(transformation_planes)
        moved_part = rs.TransformObject(new_part, transformation_planes)
        next_anchor_copy = rs.CopyObject(next_anchor)
        # moved_anchor = rs.MoveObject(next_anchor_copy, transformation_planes)
        parts.append(moved_part)
        min_dims.append(min_dimension)
        new_anchors.append(next_anchor_copy)
        rs.DeleteObjects([next_x, next_y])

    rs.DeleteObjects(normal_surfaces)
    return [parts, new_anchors, min_dims, frame_points, anchor_guids]

def remove_parts_from_groups(parts):
    for part in parts:
        groups = rs.ObjectGroups(part)
        for group in groups:
            rs.RemoveObjectFromGroup(part, group)
    

# runner function called externally by the assembly manager
def lay_parts_flat(assembly_name):
    assembly_layer_string = "SHOP::" + assembly_name
    unique_parts = extract_parts(assembly_layer_string)
    [moved_parts, anchor_points, part_thicknesses, frame_points, old_anchors] = isolate_orient_parts(unique_parts)
    create_CAM_layers(assembly_name)
    assign_moved_parts_to_CAM_layers(moved_parts, assembly_name)
    counts = get_part_counts(assembly_layer_string)
    text_objects = generate_text(assembly_name, counts, part_thicknesses, anchor_points)
    remove_parts_from_groups(moved_parts)
    rs.DeleteObjects(old_anchors)
    rs.DeleteObjects(anchor_points)






# lay_parts_flat(assembly_name, component_name)