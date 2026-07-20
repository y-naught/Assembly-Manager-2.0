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
# r: numpy
# r: scipy
# r: opencv-python


import rhinoscriptsyntax as rs
import scriptcontext as sc
import math
import numpy as np
import scipy as sp
import cv2
import copy as cp


import System
import System.Collections.Generic
import Rhino

lower_threshold = 30
upper_threshold = 100
grouping_distance_threshold = 2.0


img_path = "C:\\Users\\greg\\OneDrive\\Desktop\\The Lab Fabrication\\Important Things\\Untitled-1.png"

def load_image(image_path, low_thresh, up_thresh):
    loaded_image = cv2.imread(image_path)
    gray_image = cv2.cvtColor(loaded_image, cv2.COLOR_BGR2GRAY)
    edged_image = np.flip(np.array(cv2.Canny(gray_image, threshold1=low_thresh, threshold2=up_thresh)))
    return edged_image


def get_points(edged_image):
    points = []
    image_shape = np.shape(edged_image)
    for i in range(image_shape[0]):
        for j in range(image_shape[1]):
            if(edged_image[i][j] > 0):
                points.append(np.array([i,j]))
    return np.array(points)


def group_points(point_list):
    # we have a list of groups and a list of indices
    # groups will be a 2D array with our grouped points based on euclidian distance
    # list of indices will be a list that we remove from once a point is assigned to a group
    groups = []
    list_of_indices = []
    
    # load our list of indices
    for i in range(len(point_list)):
        list_of_indices.append(i)
    
    while(len(list_of_indices) > 0):
        current_index = list_of_indices.pop()
        # print(int(current_index))
        new_group = []
        new_group.append(current_index)
        cur_indices = cp.copy(list_of_indices)
        for i in range(len(cur_indices)):
            dist = np.linalg.norm(point_list[current_index] - point_list[cur_indices[i]])
            if(dist < grouping_distance_threshold and dist != 0):
                new_group.append(cur_indices[i])
                list_of_indices.remove(cur_indices[i])
                current_index = cp.copy(cur_indices[i])
        
        groups.append(new_group)

    return groups

def refine_groups(groups, point_list):
    merge_possible = True
    new_groups = cp.copy(groups)
    while(merge_possible):
        remove_list = []
        new_group = []
        found_group = False
        for i in range(len(new_groups)):
            for j in range(len(new_groups)):
                if(i != j):
                    for v in range(len(new_groups[i])):
                        for w in range(len(new_groups[j])):
                            dist = np.linalg.norm(point_list[new_groups[i][v]] - point_list[new_groups[j][w]])
                            if(dist < grouping_distance_threshold):
                                # merge, break and reset 
                                new_group = cp.copy(new_groups[i]) + cp.copy(new_groups[j])
                                temp_group = []
                                for k in range(len(new_groups)):
                                    if(k != i and k != j):
                                        temp_group.append(new_groups[k])
                                temp_group.append(new_group)
                                new_groups = temp_group
                                found_group = True
                                break
                        if(found_group):
                            break
                    if(found_group):
                        break                                
            if(found_group):
                break
        if(not found_group):
            merge_possible = False
    return new_groups


def sort_groups(groups, point_list):
    sorted_groups = []
    for group in groups:
        sorted_group = []
        distance_matrix = []
        if(len(group) > 2):
            two_closest_points = []
            # for each point in group
            for i in range(len(group)):
                # record keeping paramters
                closest_index = -1
                second_index = -1
                closest_point = 100000
                second_closest = 100001    

                # for each point, calc the distances to all other points in the group
                for j in range(len(group)):
                    dist = np.linalg.norm(point_list[group[i]] - point_list[group[j]])
                    if(dist > 0.001):
                        if(dist < closest_point):
                            closest_point = dist
                            closest_index = group[j]
                        elif(dist < second_closest):
                            second_closest = dist
                            second_index = group[j]

                closest_points = [[closest_index, closest_point], [second_index, second_closest]]
                two_closest_points.append(closest_points)

            for i in range(len(two_closest_points)):
                real_index = group[i]
                closest = two_closest_points[i][0]
                second = two_closest_points[i][0]
                # base case nothing has been added to group
                if(len(sorted_group) == 0):
                    sorted_group.append(real_index)
                elif(real_index not in sorted_group):
                    if(closest[0] in sorted_group):
                        insert_index = sorted_group.index(closest[0])
                        sorted_group.insert(insert_index, real_index)
                    elif(second[0] in sorted_group):
                        insert_index = sorted_group.index(second[0])
                        sorted_group.insert(insert_index, real_index)
                    else:
                        sorted_group.append(real_index)
            
                
        elif(len(group) == 1 or len(group) == 2):
            for i in range(len(group)):
                sorted_group.append(group[i])
        
        sorted_groups.append(sorted_group)

    return sorted_groups
                
                
            
            
def create_points(point_list):
    for point in point_list:
        rs.AddPoint(float(point[0]), float(point[1]), 0)

def create_curves(grouped_points, point_list):
    curve_list = []
    # print(grouped_points)
    for group in grouped_points:
        curve_point_list = []
        for points in group:
            curve_point_list.append(rs.AddPoint(float(point_list[points][0]), float(point_list[points][1]), 0))
        if(len(curve_point_list) > 1):
            curve_list.append(rs.AddCurve(curve_point_list))
    return curve_list


edged_image = load_image(img_path, lower_threshold, upper_threshold)
point_list = get_points(edged_image)
grouped_points = group_points(point_list)
print(grouped_points)
# refined_groups = refine_groups(grouped_points, point_list)
# print(refined_groups)
# sorted_groups = sort_groups(grouped_points, point_list)
points = create_points(point_list)
# curves = create_curves(sorted_groups, point_list)


