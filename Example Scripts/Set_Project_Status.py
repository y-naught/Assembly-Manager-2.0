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

import Eto.Drawing as drawing
import Eto.Forms as forms
import rhinoscriptsyntax as rs
import scriptcontext as sc
import math
from datetime import date

import System
import System.Collections.Generic
import Rhino

status_enum = [
    'Internal Review',
    'Client Review', 
    'Approved For Production', 
    'Approved For Prototyping',
    'For Vendor Estimate'
    ]

layouts = rs.LayerChildren("LAYOUTS")

current_status = rs.GetDocumentData("Lab_Data", "current_status")

if(current_status == None):
    rs.SetDocumentData("Lab_Data", "current_status", status_enum[0])


# ETO for layout
class StatusUpdateEto(forms.Dialog[bool]):

    def update_all_status(self, sender, e):
        for layout in layouts:
            try:
                newStatus = status_enum[self.statusDropdown.SelectedIndex]
                parentLayer = layout + "::Text::Status"
                layers = rs.LayerChildren(parentLayer)
                for status in layers:
                    shortName = rs.LayerName(status, fullpath=False)
                    if shortName == newStatus:
                        rs.LayerVisible(status, True)
                    else:
                        rs.LayerVisible(status, False)
            except:
                print("something fucked up.")

    def OnCancel(self, sender, e):
        self.Close(False)
    
    def __init__(self):
        if(rs.ExeVersion() == 8):
            super().__init__()

        # Form settings
        self.Title = 'Update Project Status'
        self.Padding = drawing.Padding(10)
        self.Resizable = False

        # Project Status
        self.statusLabel = forms.Label()
        self.statusLabel.Text = "Project Status"
        self.statusDropdown = forms.DropDown()
        self.statusDropdown.DataStore = [
            'Internal Review',
            'Client Review', 
            'Approved For Production', 
            'Approved For Prototyping',
            'For Vendor Estimate'
            ]
        
        self.statusDropdown.SelectedIndex = 0

        self.new_layout_button = forms.Button()
        self.new_layout_button.Text = "Update"
        self.new_layout_button.Click += self.update_all_status

        self.cancel_button = forms.Button()
        self.cancel_button.Text = "Cancel"
        self.cancel_button.Click += self.OnCancel

        # table layout
        layout = forms.DynamicLayout()
        layout.Spacing = drawing.Size(5,5)

        layout.AddRow(self.statusLabel)
        layout.AddRow(self.statusDropdown)
        layout.AddRow(None)
        layout.AddRow(self.new_layout_button, self.cancel_button)
        self.Content = layout

def runWindow():
    dialog = StatusUpdateEto()
    rc = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)
    if(rc):
        print(" ")

# python main boilerplate
if __name__ == "__main__":
    runWindow()