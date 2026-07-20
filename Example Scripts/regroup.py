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

# Dissolves all existing groups of selected parts and re groups them into a new part
def runner():
    user_objects = rs.GetObjects(
        message="Select objects you want to regroup", 
        filter=0, 
        group=True, 
        preselect=True, 
        select=True
    )

    for geometry in user_objects:
        groups = rs.ObjectGroups(geometry)
        if(groups):
            for group in groups:
                rs.DeleteGroup(group)
    
    new_group = rs.AddGroup()
    rs.AddObjectsToGroup(user_objects, new_group)


if __name__ == "__main__":
    runner()

