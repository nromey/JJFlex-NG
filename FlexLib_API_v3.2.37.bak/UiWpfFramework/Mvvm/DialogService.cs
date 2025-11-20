// ****************************************************************************
///*!	\file DialogService.cs
// *	\brief Dialog Serivce class for showing MessageBoxes
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2016-01-15
// *	\author Abed Haque, AB5ED
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

namespace Flex.UiWpfFramework.Mvvm
{
    public class DialogService : IDialogService
    {
        public MessageBoxResult ShowMessageBox(string messageBoxText, string title, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            return MessageBox.Show(messageBoxText, title, button, icon, defaultResult);
        }

        public MessageBoxResult ShowWarningYesNoBox(string messageBoxText, string title)
        {
            return MessageBox.Show(messageBoxText, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        }

        public MessageBoxResult ShowUHEBox(Exception exception, string uheTextSimple, string uheTextFull)
        {
            return MessageBox.Show(uheTextFull, "Unhandled Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }


        public MessageBoxResult ShowOkBox(string messageBoxText)
        {
            return MessageBox.Show(messageBoxText);
        }
    }
}
