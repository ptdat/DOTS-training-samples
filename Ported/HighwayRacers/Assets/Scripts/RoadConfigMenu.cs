﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;


public class RoadConfigMenu : MonoBehaviour
{
    [Header("Children")] public Text numCarsText;
    public Slider numCarsSlider;
    public Text highwaySizeText;
    public Slider highwaySizeSlider;

    public static RoadConfigMenu instance { get; private set; }

    public void Show()
    {
        gameObject.SetActive(true);
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    public void UpdateButtonPressed()
    {
        GameObject.Find("Road").GetComponent<Road>().RestartRoad(highwaySizeSlider.value, Mathf.RoundToInt(numCarsSlider.value));
    }

    public void UpdateSliderValues()
    {
    }

    // Use this for initialization
    void Awake()
    {
        if (instance != null)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;

        numCarsSlider.onValueChanged.AddListener(NumCarsSliderValueChanged);
        highwaySizeSlider.onValueChanged.AddListener(HighwaySizeSliderValueChanged);
    }

    void Start()
    {
        // update slider values
        numCarsSlider.minValue = 1;
        numCarsSlider.maxValue = (float)Road.GetMaxCars(Road.roadLength);
        
        highwaySizeSlider.minValue = Mathf.Ceil(Road.minLength);
        highwaySizeSlider.maxValue = Road.maxLength;
        
        numCarsSlider.value = Road.numCars;
        highwaySizeSlider.value = Road.roadLength;
    }

    private void NumCarsSliderValueChanged(float value)
    {
        numCarsText.text = "Number of Cars: " + Mathf.RoundToInt(value);
    }

    private void HighwaySizeSliderValueChanged(float value)
    {
        highwaySizeText.text = "Highway Size: " + Mathf.RoundToInt(value);
        numCarsSlider.maxValue = (float) Road.GetMaxCars(value);
    }

    // Update is called once per frame
    void Update()
    {
    }

    void OnDestroy()
    {
        if (instance == this)
        {
            instance = null;
        }
    }
}