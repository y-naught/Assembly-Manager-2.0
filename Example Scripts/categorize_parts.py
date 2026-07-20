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

from generate_layers import generate_layers
import Create_Assembly as ca

# gets parts from use selection
def getParts():
    OG = rs.GetObjects(message="Select Objects In Assembly", filter=16 , select=True, preselect=False)
    return OG

def get_max_index(areas):
    record = 0
    index = -1
    for i in range(len(areas)):
        if(areas[i] > record):
            index = i
            record = areas[i]
    return index

def get_normal_largest_surface(part):
    all_surfaces = rs.ExplodePolysurfaces(part)
    areas = []
    for surface in all_surfaces:
        areas.append(rs.SurfaceArea(surface)[0])
    max_index = get_max_index(areas)
    n_frame = rs.SurfaceFrame(all_surfaces[max_index],[0,0])
    max_surface = rs.CopyObject(all_surfaces[max_index])
    rs.DeleteObjects(all_surfaces)
    return [n_frame, max_surface]


def get_group_list(parts):
    group_list = []
    for part in parts:
        cur_group = rs.ObjectGroups(part)
        if(cur_group not in group_list):
            group_list.append(cur_group)
    return group_list
    

def get_direction_thickness(part, normal_plane, tolerance):
    # create bounding box oriented to part surface
    bounding_box = rs.BoundingBox(part, normal_plane)
    dims = []
    if(bounding_box):
        tempX = get_distance(bounding_box[1], bounding_box[0])
        tempY = get_distance(bounding_box[3] , bounding_box[0])
        tempZ = get_distance(bounding_box[4], bounding_box[0])
        dims.extend([round(tempX, tolerance), round(tempY, tolerance), round(tempZ, tolerance)])
    # max index seems to return 0 for X longest, 1 for z longest, and I would assume 2 for y longest
    max_index = get_max_index(dims)
    return([dims, max_index])


def compare_dimensions(dim1, dim2, tolerance):
    min_dim = abs(dim1[0] - dim2[0])
    mid_dim = abs(dim1[1] - dim2[1])
    max_dim = abs(dim1[2] - dim2[2])
    if(min_dim <= tolerance and mid_dim <= tolerance and max_dim <= tolerance):
        return True
    else:
        return False

def extract_boundary_curves(part):
    return rs.DuplicateEdgeCurves(part)

# extracts the boundary curves from each of two parts and tests them for equivalence based on lengths of ordered curves.
# returns boolean for whether they are euqivalent or not. 
def compare_boundary_curves(part1, part2, tolerance):
    bound_1 = rs.DuplicateEdgeCurves(part1)
    bound_2 = rs.DuplicateEdgeCurves(part2)
    part1_end_vectors = []
    part2_end_vectors = []
    part1_lengths = []
    part2_lengths = []
    for i in range(len(bound_1)):
        start_pt = rs.CurveStartPoint(bound_1[i])
        end_pt = rs.CurveEndPoint(bound_1[i])
        part1_end_vectors.append(rs.VectorCreate(end_pt, start_pt))
        part1_lengths.append(round(rs.CurveLength(bound_1[i]), 3))
    for i in range(len(bound_2)):
        start_pt = rs.CurveStartPoint(bound_2[i])
        end_pt = rs.CurveEndPoint(bound_2[i])
        part2_end_vectors.append(rs.VectorCreate(end_pt, start_pt))
        part2_lengths.append(round(rs.CurveLength(bound_2[i]), 3))
    part1_lengths.sort()
    part2_lengths.sort()
    rs.DeleteObjects(bound_1)
    rs.DeleteObjects(bound_2)
    if(test_list_equivalance(part1_lengths, part2_lengths, tolerance)):
        return True
    else:
        return False

def compare_boundary_curves_extracted(curves1, curves2, tolerance):
    part1_lengths = []
    part2_lengths = []
    part1_end_vectors = []
    part2_end_vectors = []
    for i in range(len(curves1)):
        start_pt = rs.CurveStartPoint(curves1[i])
        end_pt = rs.CurveEndPoint(curves1[i])
        part1_end_vectors.append(rs.VectorCreate(end_pt, start_pt))
        part1_lengths.append(round(rs.CurveLength(curves1[i]), 3))
    for i in range(len(curves2)):
        start_pt = rs.CurveStartPoint(curves2[i])
        end_pt = rs.CurveEndPoint(curves2[i])
        part2_end_vectors.append(rs.VectorCreate(end_pt, start_pt))
        part2_lengths.append(round(rs.CurveLength(curves2[i]), 3))
    part1_lengths.sort()
    part2_lengths.sort()
    if(test_list_equivalance(part1_lengths, part2_lengths, tolerance)):
        return True
    else:
        return False


# def compare_boundary_curve_lengths(curve_lengths1, curve_lengths2, tolerance):
def cleanup_boundary_curves(curves):
    for curve_set in curves:
        rs.DeleteObjects(curve_set)

def test_list_equivalance(list1, list2, tolerance):
    if(len(list1) != len(list2)):
        return False
    for i in range(len(list1)):
        if(abs(list1[i] - list2[i]) > tolerance):
            return False
    return True

# compares parts by volumes and the polysurface edges 
def categorizeParts(polysurfaces):
    dim_tolerance = 0.001
    volumes = []
    boundary_curves = []
    max_surfaces = []
    
    for part in polysurfaces:
        temp_n_frame, temp_max_surface = get_normal_largest_surface(part)
        max_surfaces.append(temp_max_surface)
        temp_bounding_dim, temp_max_index = get_direction_thickness(part, temp_n_frame, 3)
        temp_bounding_dim.sort()
        temp_volume = rs.SurfaceVolume(part)
        volumes.append(round(temp_volume[0], 3))
        boundary_curves.append(extract_boundary_curves(part))
        

    volume_comparison_array = []
    boundary_comparison_array = []

    for i in range(len(polysurfaces)):
        volume_comparison_list = []
        boundary_comparison_list = []
        for j in range(len(polysurfaces)):
            if(i != j):
                volume_comparison = volumes[i] == volumes[j]
                volume_comparison_list.append(volume_comparison)
                boundary_comparison_list.append(compare_boundary_curves_extracted(boundary_curves[i], boundary_curves[j], dim_tolerance))
            else:
                volume_comparison_list.append(None)
                boundary_comparison_list.append(None)

        volume_comparison_array.append(volume_comparison_list)
        boundary_comparison_array.append(boundary_comparison_list)
    
    cleanup_boundary_curves(boundary_curves)
    # now sort our compared elements
    indices_accounted_for = []
    categorized_parts = []

    # for each element in the comparison table
    for i in range(len(volume_comparison_array)):
        list_of_same_parts = []
        list_of_same_parts.append(i)
        num_parts = 1
        accounted = False
        # Check to see if it is already accounted for
        for counted in indices_accounted_for:
            if(i == counted):
                accounted = True
        if(accounted):
            continue
        # if not, check for all the same volumes in the list
        for j in range(len(volume_comparison_array[i])):
            volume_comparison = volume_comparison_array[i][j]
            boundary_comparison = boundary_comparison_array[i][j]
            if(boundary_comparison_array[i][j]):
                # if part has already been accounted for, skip
                if(j in indices_accounted_for):
                    continue
                else:
                    indices_accounted_for.append(j)
                    num_parts += 1
                    list_of_same_parts.append(j)
        
        # add part we compared to accounted for list
        indices_accounted_for.append(i)
        categorized_parts.append(list_of_same_parts)
    rs.DeleteObjects(max_surfaces)
    return categorized_parts


def get_distance(pt1, pt2):
    x = rs.coerce3dpoint(pt2)[0] - rs.coerce3dpoint(pt1)[0]
    y = rs.coerce3dpoint(pt2)[1] - rs.coerce3dpoint(pt1)[1]
    z = rs.coerce3dpoint(pt2)[2] - rs.coerce3dpoint(pt1)[2]
    return  (x*x + y*y + z*z)**0.5


def create_translation(parts, x_multiplier, y_multiplier):
    bounding_box = rs.BoundingBox(parts)
    dims = []
    print(bounding_box)
    if(bounding_box):
        tempX = get_distance(bounding_box[1], bounding_box[0])
        tempY = get_distance(bounding_box[3] , bounding_box[0])
        tempZ = get_distance(bounding_box[4], bounding_box[0])
        dims.extend([tempX, tempY, tempZ])
    
    max_dim = max(dims)
    x_translation = max_dim * x_multiplier
    y_translation = max_dim * y_multiplier
    translation = rs.CreateVector(x_translation, y_translation, 0)
    return translation


def generate_components(group_list, component_prefix, assembly_name):
    component_names = [["unsorted", None]]
    component_index = 1
    for group in group_list:
        component_name = component_prefix + str(component_index).zfill(2)
        ca.createComponent(assembly_name, component_name)
        component_index += 1
        if(len(group) >= 1):
            component_names.append([component_name, group[0]])
    return component_names


# sorts parts into layers
def sort_parts(volume_categorized_indices, assembly_parts, label_prefix, target_component_layer, translation):
    layer_names = generate_layers(target_component_layer, len(volume_categorized_indices), label_prefix)
    for i in range(len(volume_categorized_indices)):
        for j in range(len(volume_categorized_indices[i])):
            # sort parts onto layers
            temp_part = assembly_parts[volume_categorized_indices[i][j]]
            new_part = rs.CopyObject(temp_part, translation)
            rs.ObjectLayer(new_part, target_component_layer + "::" + layer_names[i])
    return layer_names


def sort_parts_to_components(layers, component_names, assembly_name):
    for component in component_names:
        if(component[0] != "unsorted"):
            layer_string = "SHOP::" + assembly_name + "::" + component[0]
            rs.AddLayer(layer_string)
    for part_layer in layers:
        layer_string = "SHOP::" + assembly_name + "::unsorted::" + part_layer
        parts_on_layer = rs.ObjectsByLayer(layer_string)
        for part in parts_on_layer:
            groups = rs.ObjectGroups(part)
            if(groups[0] != None):
                for i in range(len(component_names)):
                    if(groups[0] == component_names[i][1]):
                        component_layer = "SHOP::" + assembly_name + "::" + component_names[i][0] + "::" + part_layer
                        layer_color = rs.LayerColor(layer_string)
                        layer_exists = rs.LayerId(component_layer)
                        if(layer_exists == None):
                            rs.AddLayer(component_layer, color=layer_color)
                        rs.ObjectLayer(part, layer=component_layer)
            
def purge_unsorted(assembly_name):
    unsorted_layer_string = "SHOP::" + assembly_name + "::unsorted"
    unsorted_objects = rs.ObjectsByLayer(unsorted_layer_string)
    if(len(unsorted_objects) == 0):
        rs.PurgeLayer(unsorted_layer_string)

def set_document_data(assembly_name, component_details):
    assembly_layer_string = "SHOP::" + assembly_name
    component_layers = rs.LayerChildren(assembly_layer_string)
    component_list = strip_layer_to_child(component_layers)
    assembly_data = ca.getAssemblyData(assembly_name)
    assembly_description = assembly_data[0]
    new_assembly_string = ca.reconstructAssemblyString(assembly_description, component_list)
    rs.SetDocumentData("Assemblies", assembly_name, new_assembly_string)
    for component_layer in component_layers:
        component_layer_string = component_layer
        part_layers = rs.LayerChildren(component_layer_string)
        part_labels = strip_layer_to_child(part_layers)
        for component in component_details:
            if(component["name"] == strip_single_layer_to_child(component_layer)):
                parts_string = ca.reconstructComponentsString("A description goes here : ", part_labels, component["count"])
                rs.SetDocumentData("Components", component_layer, parts_string)
                break



def strip_layer_to_child(layer_list):
    stripped_list = []
    for layer in layer_list:
        layer_split_list = layer.split("::")
        stripped_list.append(layer_split_list[-1])
    return stripped_list

def strip_single_layer_to_child(layer):
    layer_split_list = layer.split("::")
    return layer_split_list[-1]


def consolidate_components(assembly_name, component_names):
    # first, let's regroup our components
    new_component_groups = []
    for component in component_names:
        new_parts = []
        if(component[1] != None):
            all_parts = rs.ObjectsByGroup(component[1])

            for part in all_parts:
                part_layer = rs.ObjectLayer(part)
                layer_split = part_layer.split("::")
                try:
                    in_assembly = layer_split.index(assembly_name)
                except ValueError:
                    in_assembly = -1
                if(in_assembly != -1):
                    new_parts.append(part)
        if(len(new_parts) > 0):
            group = rs.AddGroup()
            rs.RemoveObjectsFromGroup(new_parts, component[1])
            rs.AddObjectsToGroup(new_parts, group)
            new_component = [component[0], group]
            new_component_groups.append(new_component)
        else:
            new_component_groups.append(component)
    
    # now we can compare the parts in each group and figure out if they are the same if so, consolidate components
    # extract each component with a group and list the parts
    component_groups_with_parts = []
    for component in new_component_groups:
        component_layer_string = "SHOP::" + assembly_name + "::" + component[0]
        if(component[1] != None):
            part_layers = rs.LayerChildren(component_layer_string)

            parts_list = []
            for part_layer in part_layers:
                temp_parts = rs.ObjectsByLayer(part_layer)
                temp_part_label = strip_single_layer_to_child(part_layer)
                entry = {
                    "part_name" : temp_part_label,
                    "num_parts" : len(temp_parts)
                }
                parts_list.append(entry)
            component_groups_with_parts.append([component[0], component[1], parts_list])
    
    # now compare each set of entries to find if there are equivalent components
    equivalence_matrix = []
    for i in range(len(component_groups_with_parts)):
        matrix_column = []
        for j in range(len(component_groups_with_parts)):
            if(i != j):
                comp1 = component_groups_with_parts[i][2]
                comp2 = component_groups_with_parts[j][2]
                #first check if the have the same number of parts
                if(len(comp1) != len(comp2)):
                    matrix_column.append(False)
                else:
                    comp1_list = dict_to_tuple_list(comp1)
                    comp2_list = dict_to_tuple_list(comp2)
                    if(comp1_list == comp2_list):
                        matrix_column.append(True)
                    else:
                        matrix_column.append(False)
            else:
                matrix_column.append(None)
        equivalence_matrix.append(matrix_column)

    # finds equivalent pairs and appends them to this list
    equivalent_pairs = []
    for i in range(len(equivalence_matrix)):
        for j in range(len(equivalence_matrix[i])):
            if(i != j):
                result = equivalence_matrix[i][j]
                if(result):
                    equivalent_pairs.append([component_groups_with_parts[i][0],component_groups_with_parts[j][0]])

    # consolidates the equivalent pairs down to equivalent sets.    
    equivalent_sets = consolidate(equivalent_pairs)

    # compare the equivalent sets against the original data and add the unique sets back in
    unique_components = []
    for og_component in component_groups_with_parts:
        is_unique = True
        for component_set in equivalent_sets:
            for component in component_set:
                if(is_unique):
                    if(component == og_component[0]):
                        is_unique = False
        # after testing all components, if it is still unique append to list. 
        if(is_unique):
            unique_components.append([og_component[0]])


    # combine unique list and equivalent_sets
    full_component_set = equivalent_sets + unique_components

    consolidated_component_names = []
    # consolidate components and the parts on their sub-layers
    for component_set in full_component_set:
        first_component = component_set[0]
        if(len(component_set) > 1):
            target_component_string = "SHOP::" + assembly_name + "::" + first_component
            # for remaining components in this set, consolidate layers to first component
            for i in range(1, len(component_set)):
                component_identifier = component_set[i]
                current_component_string = "SHOP::" + assembly_name + "::" + component_identifier
                part_layers = rs.LayerChildren(current_component_string)
                for part in part_layers:
                    part_identifier = strip_single_layer_to_child(part)
                    target_part_string = target_component_string + "::" + part_identifier
                    current_layer_parts = rs.ObjectsByLayer(part)
                    for current_part in current_layer_parts:
                        rs.ObjectLayer(current_part, target_part_string)
                rs.PurgeLayer(current_component_string)
        consolidated_component_names.append(first_component)
    
    # rename layers based on their identifiers
    prefix = extract_prefix(consolidated_component_names[0])
    new_component_names = []


    for i in range(len(consolidated_component_names)):
        new_component_name = prefix + str(i+1).zfill(2)
        old_component_layer_string = "SHOP::" + assembly_name + "::" + consolidated_component_names[i]
        new_component_layer_string = "SHOP::" + assembly_name + "::" + new_component_name
        new_layer = rs.RenameLayer(old_component_layer_string, new_component_name)
        new_component_names.append(new_component_name)


    # now return the number of objects in a component
    component_details = []
    print("compare new_component_names and full_component_set")
    print(new_component_names)
    print(full_component_set)
    for i in range(len(new_component_names)):
        entry = {
            "name" : new_component_names[i],
            "count" : len(full_component_set[i])
        }
        component_details.append(entry)
    
    return component_details


def extract_prefix(part_name):
    new_part_name = part_name.replace("TEMP_", "")
    return new_part_name[0:len(new_part_name)-2]



## consolidation of list code
def find(parent, i):
    if parent[i] == i:
        return i
    else:
        # Path compression heuristic
        parent[i] = find(parent, parent[i])
        return parent[i]


# used in the consolidate pairs of components system
def union(parent, rank, x, y):
    rootX = find(parent, x)
    rootY = find(parent, y)

    if rootX != rootY:
        # Union by rank heuristic
        if rank[rootX] > rank[rootY]:
            parent[rootY] = rootX
        elif rank[rootX] < rank[rootY]:
            parent[rootX] = rootY
        else:
            parent[rootY] = rootX
            rank[rootX] += 1

# consolidates pairs of equivalent parts to lists of equivalent parts
def consolidate(pairs):
    parent = {}
    rank = {}
    
    # Initialize parent and rank dictionaries
    for pair in pairs:
        for item in pair:
            if item not in parent:
                parent[item] = item
                rank[item] = 0
    
    # Apply union operation on each pair
    for x, y in pairs:
        union(parent, rank, x, y)

    # Find the root representative of each item and group them
    root_map = {}
    for item in parent:
        root = find(parent, item)
        if root not in root_map:
            root_map[root] = []
        root_map[root].append(item)
    
    return list(root_map.values())



# helper function for checking equivalance of components
def dict_to_tuple_list(obj):
    return sorted((d['part_name'], d['num_parts']) for d in obj)


# main running function called by the Assembly Manager
def generate_parts(part_label_prefix, component_prefix, assembly_name, translation_multiplier):
    parts = getParts()
    group_list = get_group_list(parts)
    component_names = generate_components(group_list, "TEMP_" + component_prefix, assembly_name)
    initial_component_layer = "SHOP::" + assembly_name + "::" + component_names[0][0]
    categorized_part_indices = categorizeParts(parts)
    new_parts_translation = create_translation(parts, translation_multiplier, translation_multiplier)
    layers = sort_parts(categorized_part_indices, parts, part_label_prefix, initial_component_layer, new_parts_translation)
    sort_parts_to_components(layers, component_names, assembly_name)
    purge_unsorted(assembly_name)
    component_details = consolidate_components(assembly_name, component_names)
    set_document_data(assembly_name, component_details)
    return layers

