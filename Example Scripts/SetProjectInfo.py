#! python3

import rhinoscriptsyntax as rs
import Rhino
import Rhino.UI
import Eto.Drawing as drawing
import Eto.Forms as forms

# Define the keys required for the project info
KEYS = [
    "PROJECT NAME",
    "PROJECT #",
    "CLIENT",
    "DELIVERABLE",
    "DELIVERABLE #",
    "REVISION",
    "STATUS",
    "PROJECT MANAGER NAME",
    "DESIGNER NAME",
    "MISC.",
]

class SetProjectInfoDialog(forms.Dialog[bool]):
    def __init__(self):
        # Support for Rhino 8+
        if hasattr(rs, 'ExeVersion') and rs.ExeVersion() >= 8:
            super().__init__()
            
        self.Title = 'Set Project Info'
        self.Padding = drawing.Padding(15)
        self.Resizable = False

        self.textboxes = {}
        
        # Setup the layout
        layout = forms.DynamicLayout()
        layout.Spacing = drawing.Size(5, 5)

        # Create a text box for each key
        for key in KEYS:
            label = forms.Label()
            label.Text = key
            textbox = forms.TextBox()
            textbox.Size = drawing.Size(200, 25)
            
            # Fetch existing document user text if it exists
            try:
                existing_text = rs.GetDocumentUserText(key)
                if existing_text:
                    textbox.Text = existing_text
                else:
                    textbox.Text = ""
            except Exception as e:
                textbox.Text = ""
                print("Error retrieving key {}: {}".format(key, e))

            self.textboxes[key] = textbox
            layout.AddRow(label, textbox)

        # Spacer row
        layout.AddRow(None)

        # Buttons
        self.update_button = forms.Button()
        self.update_button.Text = "Update"
        self.update_button.Click += self.OnUpdateButton

        self.cancel_button = forms.Button()
        self.cancel_button.Text = "Cancel"
        self.cancel_button.Click += self.OnCancelButton

        # Add buttons to layout
        layout.AddRow(self.update_button, self.cancel_button)
        
        self.Content = layout

    def OnUpdateButton(self, sender, e):
        # Apply the values from the textboxes to document user text
        for key in KEYS:
            try:
                textbox = self.textboxes[key]
                rs.SetDocumentUserText(key, textbox.Text)
            except Exception as ex:
                print("Error setting key {}: {}".format(key, ex))
                
        self.Close(True)

    def OnCancelButton(self, sender, e):
        self.Close(False)

def run_script():
    dialog = SetProjectInfoDialog()
    rc = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)
    if rc:
        print("Document User Text successfully updated.")

if __name__ == "__main__":
    run_script()
