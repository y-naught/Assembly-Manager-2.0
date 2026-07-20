import rhinoscriptsyntax as rs
import random
from System.Drawing import Color
import Rhino
import scriptcontext
import System
import Rhino.UI
import Eto.Drawing as drawing
import Eto.Forms as forms


colors = []

pre_defined_colors = [
    (230, 25, 75), (60, 180, 75), (255, 225, 25), 
    (0, 130, 200), (245, 130, 48), (145, 30, 180), 
    (70, 240, 240), (240, 50, 230), (210, 245, 60), 
    (250, 190, 212), (0, 128, 128), (220, 190, 255), 
    (170, 110, 40), (255, 250, 200), (128, 0, 0), 
    (170, 255, 195), (128, 128, 0), (255, 215, 180), 
    (0, 0, 128), (128, 128, 128), (255, 0, 0)
    ]

def random_color():
    red = int(255*random.random())
    green = int(255*random.random())
    blue = int(255*random.random())
    return Color.FromArgb(red,green,blue)


def generate_layers(parent_layer, n_parts, prefix):
    layer_names = []
    print("n_parts : ", n_parts)
    for i in range(n_parts):
        layer_name = prefix + str(i+1).zfill(2)
        if(i < len(pre_defined_colors) - 2):
            print("pre-defined color")
            rs.AddLayer(name=layer_name, color = pre_defined_colors[i], visible=True, locked=False, parent=parent_layer)
        else:
            print("random_color")
            rs.AddLayer(name=layer_name, color = random_color(), visible=True, locked=False, parent=parent_layer)
        layer_names.append(layer_name)
    return layer_names


def generate_layers_random(parent_layer, n_parts, prefix):
    layer_names = []
    for i in range(n_parts):
        layer_name = prefix + str(i+1).zfill(2)
        print("random_color")
        rs.AddLayer(name=layer_name, color = random_color(), visible=True, locked=False, parent=parent_layer)
        layer_names.append(layer_name)
    return layer_names

class SampleEtoDialog(forms.Dialog[bool]):
    # dialog box initializer. Everythign we need to define to create the box goes here.
    def __init__(self):
        if(rs.ExeVersion() == 8):
            super().__init__()
        self.Title = "name of window"
        self.Padding = drawing.padding(10)
        self.Resizable = False
        
        # Add individual elements (textbox example)
        self.control_1_label = forms.Label()
        self.control_1_label.Text = "Text for label"
        self.control_1_textbox = forms.TextBox()
        self.control_1_textbox.Text = "none"

        self.DefaultButton = forms.Button()
        self.DefaultButton.Text = 'OK'
        self.DefaultButton.Click += self.OnOKButtonClick # Event trigger here
        
        # Create a table layout and add controls to the table
        layout = forms.DynamicLayout()
        layout.Spacing = drawing.Size(5,5)
        layout.AddRow(self.control_1_label, self.control_1_textbox)
        layout.AddRow(None) #creates a spacer
        layout.AddRow(self.DefaultButton)
        
        # set the dialog content to the layout you created
        self.Content = layout

        # Class functions go here
        def OnOkButtonClick(self, sender, e):
            self.doSomethingHere()




# The script that will use that dialog class we just defined
def RunDialog():
    dialog = SampleEtoDialog()
    rc = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)
    if(rc):
        print(dialog.returnFunction())

# Boilerplate for main
if __name__ == "__main__":
	RunDialog()
