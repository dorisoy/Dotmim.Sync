using System;
using System.Collections.Generic;
using Xamarin.Forms;
using XamSyncSample.ViewModels;
using XamSyncSample.Views;

namespace XamSyncSample
{
    public partial class AppShell : Xamarin.Forms.Shell
    {
        public AppShell()
        {
            InitializeComponent();
        }

        private async void OnMenuItemClicked(object sender, EventArgs e)
        {
            await Shell.Current.GoToAsync("//LoginPage");
        }
    }
}
