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
import json

import System
import System.Collections.Generic
import Rhino


def store_data(section, entry, json):
    json_string = json.dumps(json)
    previous_value = rs.SetDocumentData(section, entry, json_string)
    if(previous_value != json_string):
        print("Updated ", section, ":", entry)
    else:
        print("Samve value")

def load_data(section, entry):
    data = rs.GetDocumentData(section, entry)
    if(data == None):
        return None
    else:
        return json.load(data)

