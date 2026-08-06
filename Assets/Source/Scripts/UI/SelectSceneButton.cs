using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class SelectSceneButton : MonoBehaviour
{
    [SerializeField] private string _sceneName;

    private Button _button;
    private UnityAction ButtonPressed;

    private void OnEnable()
    {
        _button = GetComponent<Button>();
        ButtonPressed += OnButtonPressed;
        _button.onClick.AddListener(ButtonPressed);
    }

    private void OnDisable()
    {
        if (_button != null)
            _button.onClick.RemoveListener(ButtonPressed);
        ButtonPressed -= OnButtonPressed;
    }

    private void OnButtonPressed()
    {
        SceneManager.LoadScene(_sceneName);
    }
}