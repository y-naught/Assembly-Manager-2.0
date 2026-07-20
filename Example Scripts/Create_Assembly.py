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

import categorize_parts as cp

# retrieves the data from document (assemblies live on parent setup)
def getAssemblies():
    # gets whatever assemblies exist
    assembly_list = rs.GetDocumentData("Assemblies")
    return assembly_list


# Generic function that checks for what names already exist and makes sure there are no naming collisions
def checkNameCollision(list_names, new_name):
    if(list_names is not None):
        for ass in list_names:
            if(ass == new_name):
                return True
    return False

# Checks for component naming collisions in an assembly
# This function is sort of useless as there is no current way to manually add components to an assembly
def checkComponentCollision(assy_name, component_name):
    assembly_data = getAssemblyData(assy_name)
    for component in assembly_data[1]:
        if(component == component_name):
            return True
    return False

# used for concatenating a string from a list
def convert_list_to_string(old_list):
    new_list = ''.join(str(x) for x in old_list)
    return new_list

# removes an assembly from the system. This includes within document data and layers
def removeAssembly(assy_name):
    assy = getAssemblies()
    for ass in assy:
        if(ass == assy_name):
            components_to_remove = getComponents(ass)
            for component in components_to_remove:
                component_name = "SHOP::" + assy_name + "::" + component
                rs.DeleteDocumentData("Components", component_name)
            rs.DeleteDocumentData("Assemblies", assy_name)
            removeAssyLayer(assy_name)
            return True
    return False

# creates inital assembly string with delimeters with info
def initAssemblyString(description):
    categoryDelimeter = "$$"
    componentDelimeter = "%%"
    descString = "Description : " + description
    compString = "Components : " + componentDelimeter
    fullString = descString + categoryDelimeter + compString
    return fullString

# stringifying system for assembly strings
def reconstructAssemblyString(description, componentArray):
    categoryDelimeter = "$$"
    componentDelimeter = "%%"
    descString = "Description : " + description
    compString = "Components : "
    for component in componentArray:
        temp_string = componentDelimeter + component
        compString += temp_string
    fullString = descString + categoryDelimeter + compString
    return fullString

# tool for deconstruction assembly string into usefull schemas
def deconstructAssemblyString(assyString):
    categoryDelimeter = "$$"
    componentDelimeter = "%%"
    categoryArray = assyString.split(categoryDelimeter)
    description = categoryArray[0].replace("Description : ", "")
    components = categoryArray[1].replace("Components : ", "")
    componentsArray = components.split(componentDelimeter)
    cleaned_components = componentsArray[1:len(componentsArray)]
    return [description, cleaned_components]

# stringifying system for component data storage
def reconstructComponentsString(description, part_array, quantity):
    part_delimeter = "@@"
    categoryDelimeter = "$$"
    descString = "Description : " + description
    part_string = "Parts : "
    quantity_string = "Quantity : " + str(quantity)
    for part in part_array:
        temp_string = part_delimeter + part
        part_string += temp_string
    fullString = descString + categoryDelimeter + part_string + categoryDelimeter + quantity_string
    return fullString

# tool for deconstructing a stored component string into useable data
def deconstructComponentsString(compString):
    part_delimeter = "@@"
    categoryDelimeter = "$$"
    if(compString != None):
        categoryArray = compString.split(categoryDelimeter)
        description = categoryArray[0].replace("Description : ", "")
        parts = categoryArray[1].replace("Parts : ", "")
        if(len(categoryArray) > 2):
            quantity = int(categoryArray[2].replace("Quantity : ", ""))
        else:
            quantity = None
        componentsArray = parts.split(part_delimeter)
        if(componentsArray[0] == ""):
            cleaned_parts_array = componentsArray[1:len(componentsArray)]
        else:
            cleaned_parts_array = componentsArray
        return [description, cleaned_parts_array, quantity]
    else:
        return[None, None, None]

# gets the child layers of an assembly
def getAssemblyLayers(assy_name):
    assembly_children = rs.LayerChildren("SHOP::" + assy_name)
    return assembly_children

# checks components within an assembly for collisions
def checkAssemblyLayers(assy_name, new_layer):
    assembly_children = getAssemblyLayers(assy_name)
    for children in assembly_children:
        if(children == new_layer):
            return True
    return False

# returns the data array for the assembly components
def getAssemblyData(assy_name):
    assembly_data_string = rs.GetDocumentData("Assemblies", assy_name)
    array_data = deconstructAssemblyString(assembly_data_string)
    # print(array_data)
    return array_data


# modifies the assembly data in (Assemblies) section
def addComponentToAssembly(assy_name, component_name):
    assembly_data = getAssemblyData(assy_name)
    assembly_data[1].append(component_name)
    while('' in assembly_data[1]):
        assembly_data[1].remove('')
    new_assembly_string = reconstructAssemblyString(assembly_data[0], assembly_data[1])
    rs.SetDocumentData("Assemblies", assy_name, new_assembly_string)


# removes a component from an assembly string
def removeComponentFromAssembly(assy_name, component_name):
    assembly_data = getAssemblyData(assy_name)
    assembly_data[1].remove(component_name)
    while('' in assembly_data[1]):
        assembly_data[1].remove('')
    new_assembly_string = reconstructAssemblyString(assembly_data[0], assembly_data[1])
    print(new_assembly_string)
    rs.SetDocumentData("Assemblies", assy_name, new_assembly_string)

# creates a component for an assembly
def createComponent(assy_name, component_name):
    # will eventually have a parts categorizer and generator involved
    component_string = assy_name + "&&" + component_name
    # check to see if the component has a naming collision in the assembly
    if(not checkComponentCollision(assy_name, component_name) and not checkAssemblyLayers(assy_name, component_name)):
        # create a new entry for component
        rs.AddLayer(component_name, parent="SHOP::" + assy_name)
        addComponentToAssembly(assy_name, component_name)
    else:
        print("Component naming collision!")

# returns components that are stored in document data object
def getComponents(assembly_name):
    assembly_data = getAssemblyData(assembly_name)
    return assembly_data[1]

# returns the data from a component
def getCompData(assy_name, component_name):
    component_full_name = "SHOP::" + assy_name + "::" + component_name
    component_data = rs.GetDocumentData("Components", component_full_name)
    deconstructed_data = deconstructComponentsString(component_data)
    if(deconstructed_data):
        return deconstructed_data
    else:
        deconstructed_data[1].remove('')
        return deconstructed_data

# removes the named component from specific assembly
def removeComponent(assy_name, component_name):
    component_string = assy_name + "&&" + component_name
    rs.PurgeLayer("SHOP::" + assy_name + "::" + component_name)
    rs.DeleteDocumentData("Components", component_string)
    removeComponentFromAssembly(assy_name, component_name)


# returns child layers from SHOP Master Layer
def getShopLayers():
    shop_children = rs.LayerChildren("SHOP")
    return shop_children


# create layer with the SHOP layer being the parent
def createAssyLayer(assy_name):
    rs.AddLayer(assy_name, parent="SHOP")


# removes layer from SHOP parent
def removeAssyLayer(assy_name):
    if(rs.LayerId("SHOP::" + assy_name) != None):
        rs.PurgeLayer("SHOP::" + assy_name)
    if(rs.LayerId("CAM::" + assy_name) != None):
        rs.PurgeLayer("CAM::" + assy_name)
    if(rs.LayerId("DRAWINGS::" + assy_name) != None):
        rs.PurgeLayer("DRAWINGS::" + assy_name)


# creates an assembly, checks for naming collisions, updates layer structure and document data with system.
def createAssembly(assy_name):
    # check for assemblies that already exist
    assy = getAssemblies()
    if(not checkNameCollision(assy, assy_name)):
        # Create assembly in document data
        dataString = initAssemblyString("Description will go here")
        rs.SetDocumentData("Assemblies", assy_name, dataString)
        createAssyLayer(assy_name)
        # add assembly to assembly list
        print("Successfull Created Assembly!")
    else:
        print("Naming Collision Detected, we did not create an assembly!")

