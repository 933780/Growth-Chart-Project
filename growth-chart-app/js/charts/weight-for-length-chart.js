/* global GC, Chart, jQuery */
/*
 * Weight-for-Length/Height Chart
 * X-axis: Length/Height (cm or inches)
 * Y-axis: Weight (kg or lb)
 * Data source: WHO_WFL (0-5 years)
 */
(function(NS, $) {
    "use strict";
    var NAME = "Weight for Length Chart";

    // Rational approximation of the inverse normal CDF (qnorm / probit).
    // Converts a percentile (0–1) to a Z-score.
    // Accuracy: |error| < 4.5e-4 over the full range.
    // Source: Abramowitz & Stegun, 26.2.23
    function percentileToZ(p) {
        var c0 = 2.515517, c1 = 0.802853, c2 = 0.010328,
            d1 = 1.432788, d2 = 0.189269, d3 = 0.001308,
            t, num, den, z;

        if (p <= 0) { return -Infinity; }
        if (p >= 1) { return  Infinity; }

        var upper = (p > 0.5);
        var q = upper ? (1 - p) : p;

        t   = Math.sqrt(-2 * Math.log(q));
        num = c0 + t * (c1 + t * c2);
        den = 1  + t * (d1 + t * (d2 + t * d3));
        z   = t - num / den;

        return upper ? z : -z;
    }

    // LMS formula: given L, M, S and a percentile, return the measurement value.
    // WHO standard: X = M * (1 + L*S*Z)^(1/L)   when L != 0
    //               X = M * exp(S*Z)              when L == 0
    function lmsToValue(L, M, S, percentile) {
        var Z = percentileToZ(percentile);
        var X;
        if (Math.abs(L) < 1e-6) {
            X = M * Math.exp(S * Z);
        } else {
            var inner = 1 + L * S * Z;
            if (inner <= 0) { return null; }
            X = M * Math.pow(inner, 1 / L);
        }
        return (isFinite(X) && X > 0) ? X : null;
    }

    // Convert kg→lb or cm→in for "eng" (imperial) mode
    function kgToLb(kg) { return kg * 2.20462; }
    function cmToIn(cm) { return cm * 0.393701; }

    function WeightForLengthChart() {
        this.settings  = GC.chartSettings.wflChart;
        this._nodes    = [];
        this.__CACHE__ = {};
    }

    WeightForLengthChart.prototype = new Chart();

    $.extend(WeightForLengthChart.prototype, {

        title : NAME,

        xAxisType : "length",

        patientDataType : "weight",

        getUnits : function() {
            return GC.App.getMetrics() === "eng" ? "lb" : "kg";
        },

        // Return percentile and z for a given measured value at an x-domain value
        // For WFL, xDomainVal is Length (cm), value is weight (kg)
        getPercentileAt: function(value, xDomainVal) {
            var ds = this._primaryDataSet;
            if (!ds) { return { pct: null, z: null }; }

            var gender = GC.App.getGender();
            var data = ds.data[gender];
            var len = data.length;
            var i;

            // Find surrounding LMS entries by Length
            for (i = 0; i < len; i++) {
                if (data[i].Length === xDomainVal) {
                    var L = data[i].L, M = data[i].M, S = data[i].S;
                    var z = (Math.abs(L) < 1e-6) ? Math.log(value / M) / S : (Math.pow(value / M, L) - 1) / (L * S);
                    var pct = isFinite(z) ? Math.normsdist(z) : null;
                    return { pct: pct, z: z };
                }
                if (i < len - 1 && xDomainVal > data[i].Length && xDomainVal <= data[i+1].Length) {
                    var w = (xDomainVal - data[i].Length) / (data[i+1].Length - data[i].Length);
                    L = data[i].L * (1 - w) + data[i+1].L * w;
                    M = data[i].M * (1 - w) + data[i+1].M * w;
                    S = data[i].S * (1 - w) + data[i+1].S * w;
                    z = (Math.abs(L) < 1e-6) ? Math.log(value / M) / S : (Math.pow(value / M, L) - 1) / (L * S);
                    pct = isFinite(z) ? Math.normsdist(z) : null;
                    return { pct: pct, z: z };
                }
            }

            return { pct: null, z: null };
        },

        // Provide patient points: keep `agemos` as months for lookups and add
        // `length` property for X mapping.
        getPatientDataPoints: function() {
            var patient = GC.App.getPatient();
            if (!patient) { return null; }

            var isEng      = GC.App.getMetrics() === "eng";
            var weightData = patient.data.weight           || [];
            var lengthData = patient.data.lengthAndStature || [];
            var pts = [];

            $.each(weightData, function(i, wEntry) {
                $.each(lengthData, function(j, lEntry) {
                    if (Math.abs(wEntry.agemos - lEntry.agemos) <= 0.25) {
                        var lv = isEng ? cmToIn(lEntry.value) : lEntry.value;
                        var wv = isEng ? kgToLb(wEntry.value) : wEntry.value;
                        // Keep agemos as months (for patient model lookups) and
                        // add `length` for horizontal placement on WFL charts.
                        pts.push({ agemos: wEntry.agemos, value: wv, length: lv });
                        return false; // break inner loop
                    }
                });
            });

            var ps = new PointSet(pts, "agemos", "value");
            return ps.compact();
        },

        // Override drawPatientData to use `point.length` for X coordinate
        // while keeping `agemos` for record lookup and annotations.
        drawPatientData: function() {
            var pointSet = this.getPatientDataPoints(),
                patient,
                lastPoint,
                p,// the line
                dots,
                inst,
                elem,
                x, y, entry;

            if ( !pointSet || !pointSet._length ) {
                return;
            }

            patient   = GC.App.getPatient();
            lastPoint = pointSet._originalData[pointSet._originalData.length - 1];
            p         = [];
            dots      = [];
            inst      = this;

            // Iterate over each point
            pointSet.forEach(function( point, i/*, points*/ ) {

                // Use `length` for X placement when available, otherwise fall back
                // to the standard age months value.
                var xVal = point.length !== undefined ? point.length : point.agemos;

                // Find the X/Y coordinates of the current point
                x = inst._scaleX( xVal );
                y = inst._scaleY( point.value  );

                // Register this point as line point
                p[i] = [ x, y ];

                // Nothing more needs to be done for virtual points, so just
                // continue with the next iteration
                if ( point.virtual ) {
                    return true;
                }

                // Gestational Arrows
                inst.drawGestArrow({
                    startX      : x,
                    startY      : y,
                    curAgemos   : point.agemos,
                    curValue    : point.value,
                    isLastPoint : point === lastPoint
                });

                // Draw the dot
                entry = patient.getModelEntryAtAgemos(point.agemos);
                elem = inst.drawDot(x, y, {
                    firstMonth : point.agemos <= 1,
                    annotation : entry && entry.annotation,
                    point      : point,
                    record     : entry
                }).toFront();
                // Make this dot behave like selection points so hover tooltips
                // are shown correctly for length-based charts.
                // ensure the element is marked as a tooltip-point for CSS/handlers
                if (typeof elem.addClass === "function") {
                    elem.addClass("tooltip-point");
                } else {
                    try { elem.node.classList.add("tooltip-point"); } catch (e) {}
                }

                (function(el, pt, px, py) {
                    var tt = null;
                    el.hover(function() {
                        // mouseenter
                        var pctz = GC.App.getPCTZ();
                        var pct = null, z = null, text2 = "N/A", bg;
                        if (typeof inst.getPercentileAt === "function") {
                            var res = inst.getPercentileAt(pt.value, pt.length !== undefined ? pt.length : pt.agemos);
                            pct = res && res.pct !== undefined ? res.pct : null;
                            z   = res && res.z   !== undefined ? res.z   : null;
                        }
                        // Create two-tone tooltip: grey main, color accent for text2
                        bg = Raphael.color(inst.settings.color);
                        bg.s = Math.min(1, bg.s * 1.1);
                        bg.l = Math.max(0, bg.l / 1.1);
                        bg = Raphael.hsl(bg.h, bg.s, bg.l);

                        if (pctz == "pct" && pct !== null && isFinite(pct)) {
                            text2 = GC.Util.format(pct * 100, { type: "percentile" });
                        } else if (pctz == "z" && z !== null && isFinite(z)) {
                            text2 = GC.Util.format(z, { type: "zscore" });
                        }
                        text2 = (text2 === undefined || text2 === null) ? "N/A" : String(text2);

                        try {
                            if (!isFinite(px) || !isFinite(py)) {
                                console.warn("WFL tooltip: invalid coords, skipping tooltip", px, py);
                            } else {
                                // Include length/height in the tooltip so users can verify actual values
                                var lengthLabel = "N/A";
                                if (pt.length !== undefined) {
                                    // GC.Util.format already includes units (cm/in) when appropriate
                                    lengthLabel = GC.Util.format(pt.length, { type: "length" });
                                } else if (pt.agemos !== undefined) {
                                    lengthLabel = GC.Util.format(pt.agemos, { type: "ageMonths" });
                                }

                                // Merge length into the main text for consistent formatting
                                var mainText = inst.getTooltipLabel(pt.value);
                                if (lengthLabel && lengthLabel !== "N/A") {
                                    mainText = mainText + " • " + lengthLabel;
                                }
                                tt = GC.tooltip(inst.pane.paper, {
                                    x: px,
                                    y: py,
                                    shiftY: 30,
                                    shadowOffsetX: -15,
                                    shadowOffsetY: 5,
                                    bg: GC.chartSettings.hoverSelectionLine.stroke,
                                    text: mainText,
                                    text2: text2,
                                    text2bg: bg
                                });
                            }
                        } catch (e) {
                            console.error("WFL tooltip creation failed", e, text2);
                        }
                    }, function() {
                        // mouseleave
                        if (tt) {
                            tt.remove();
                            tt = null;
                        }
                    });
                })(elem, point, x, y);
                // Wire click to select the corresponding age across charts
                try {
                    (function(el, pt) {
                        el.click(function() {
                            if (pt && pt.agemos !== undefined && GC.App && GC.App.ChartsView) {
                                GC.App.ChartsView.selectAge(GC.Util.months2weeks(pt.agemos), "selected");
                            }
                        });
                    })(elem, point);
                } catch (e) { /* ignore if element doesn't support click */ }
                inst._nodes.push(elem);
                dots.push(elem);

            });

            // Draw line if there are more than one points
            if ( pointSet._length > 1 ) {

                if ( pointSet._data[pointSet._length - 1].virtual ) {

                    this._nodes.push(this._drawGradientLine(
                        p.pop(),
                        p[p.length - 1],
                        GC.chartSettings.patientData.lines["stroke-width"],
                        "#000",
                        GC.Util.mixColors(
                            this.settings.fillRegion.fill,
                            "#FFF",
                            this.settings.fillRegion["fill-opacity"]
                        )
                    ));

                }

                p = "M" + p.join("L");
                elem = this.pane.paper.path(p)
                    .attr(GC.chartSettings.patientData.lines);
                this._nodes.push(elem.toFront());
            }

            // Move the dots on top
            $.each(dots, function() { this.toFront(); });

            return this;
        },

        getXUnits : function() {
            return GC.App.getMetrics() === "eng" ? "in" : "cm";
        },

        getTitle : function() {
            return "Weight for Length/Height (" + this.getXUnits() + "/" + this.getUnits() + ")";
        },

        // Return null for subtitle to hide the WHO age range label (not applicable to WFL)
        getSubtitle : function() {
            return null;
        },

        // Don't draw the WHO/age range watermark for WFL since it uses length, not age
        drawWaterMark : function() {
            return this;
        },

        // Suppress the right-end percentile/z-score labels that the base Chart
        // draws at the last point of each curve. WFL uses length as X, so those
        // labels render inside the chart area rather than at a right-axis edge.
        drawDataLineLabel : function() {
            return this;
        },

        // Override draw to skip rendering if there's no patient data or dataSet not set
        draw : function() {
            var patient = GC.App.getPatient();
            // Only draw if patient exists AND dataSet is set (meaning setDataSource was called)
            if (!patient || !this.dataSet) {
                return this;
            }
            return Chart.prototype.draw.call(this);
        },

        // Override X scaling for length-based charts: map Length domain to chart X
        _scaleX : function(n) {
            var bounds = this.get("dataBounds");
            var minX = bounds.minX;
            var maxX = bounds.maxX;
            // Fallback to reasonable range if bounds are invalid
            if (!isFinite(minX) || !isFinite(maxX) || minX === maxX) {
                minX = 0; maxX = 100;
            }
            return GC.Util.scale(n, minX, maxX, this.x, this.x + this.width);
        },

        setDataSource : function(src) {
            // WFL uses Length as x-axis (not Agemos) so GC.getDataSet
            // filters by age range and cannot find it — assign directly.
            var dsName = src + "_WFL";
            var ds = GC.DATA_SETS[dsName];
            if (ds) {
                this._primaryDataSet = ds;
                // also set the base `dataSet` to the dataset name so the
                // Chart base class can locate curves via GC.DATA_SETS[this.dataSet]
                this.dataSet = dsName;
            }
            return this;
        },

        setProblem : function(src) {
            var dsName = src + "_WFL";
            var ds = GC.DATA_SETS[dsName];
            if (ds) {
                this._secondaryDataSet = ds;
                this.problemDataSet = dsName;
            }
            return this;
        },

        // Generate percentile curve points using direct LMS calculation.
        // Does NOT call GC.findXFromPercentile (which is age-keyed, not length-keyed).
        generateCurveSeries : function(dataSet, gender, percentile) {
            var data    = dataSet.data[gender],
                len     = data.length,
                isEng   = GC.App.getMetrics() === "eng",
                points  = [],
                i, entry, lengthVal, weightVal;

            for (i = 0; i < len; i++) {
                entry     = data[i];
                weightVal = lmsToValue(entry.L, entry.M, entry.S, percentile);
                if (weightVal === null) { continue; }

                lengthVal = entry.Length;

                if (isEng) {
                    lengthVal = cmToIn(lengthVal);
                    weightVal = kgToLb(weightVal);
                }

                points.push({ x: lengthVal, y: weightVal });
            }

            return points;
        },

        // Patient data points: match weight and length observations by age.
        _get_dataPoints : function() {
            var patient = GC.App.getPatient();
            if (!patient) { return []; }

            var isEng      = GC.App.getMetrics() === "eng";
            var weightData = patient.data.weight           || [];
            var lengthData = patient.data.lengthAndStature || [];
            var points     = [];

            $.each(weightData, function(i, wEntry) {
                $.each(lengthData, function(j, lEntry) {
                    if (Math.abs(wEntry.agemos - lEntry.agemos) <= 0.25) {
                        var lv = isEng ? cmToIn(lEntry.value) : lEntry.value;
                        var wv = isEng ? kgToLb(wEntry.value) : wEntry.value;
                        points.push({ x: lv, y: wv, agemos: wEntry.agemos });
                        return false; // break inner $.each
                    }
                });
            });

            return points.sort(function(a, b) { return a.x - b.x; });
        },

        // Override dataBounds computation to use length (X) instead of agemos
        _get_dataBounds : function()
        {
            var out = {
                    minX : [ Number.MAX_VALUE ],
                    maxX : [ Number.MIN_VALUE ],
                    minY : [ Number.MAX_VALUE ],
                    maxY : [ Number.MIN_VALUE ]
                }, range, minLength, maxLength, minWeight, maxWeight;

            // For WFL, compute bounds based on length (X) and weight (Y) from patient data
            var patient = GC.App.getPatient();
            if (patient) {
                var isEng = GC.App.getMetrics() === "eng";
                var weightData = patient.data.weight || [];
                var lengthData = patient.data.lengthAndStature || [];
                
                minLength = Number.MAX_VALUE;
                maxLength = Number.MIN_VALUE;
                minWeight = Number.MAX_VALUE;
                maxWeight = Number.MIN_VALUE;
                
                $.each(weightData, function(i, wEntry) {
                    $.each(lengthData, function(j, lEntry) {
                        if (Math.abs(wEntry.agemos - lEntry.agemos) <= 0.25) {
                            var lv = isEng ? cmToIn(lEntry.value) : lEntry.value;
                            var wv = isEng ? kgToLb(wEntry.value) : wEntry.value;
                            minLength = Math.min(minLength, lv);
                            maxLength = Math.max(maxLength, lv);
                            minWeight = Math.min(minWeight, wv);
                            maxWeight = Math.max(maxWeight, wv);
                            return false;
                        }
                    });
                });
                
                if (minLength !== Number.MAX_VALUE) {
                    out.minX.push(minLength);
                    out.maxX.push(maxLength);
                    out.minY.push(minWeight);
                    out.maxY.push(maxWeight);
                }
            }

            // For WFL curves, use the primaryDataRange computed from curve data
            if ( this.dataSet ) {
                range = this.get("primaryDataRange");
                out.minX.push(range.minX);
                out.minY.push(range.minY);
                out.maxX.push(range.maxX);
                out.maxY.push(range.maxY);
            }

            if ( this.problemDataSet ) {
                range = this.get("secondaryDataRange");
                out.minX.push(range.minX);
                out.minY.push(range.minY);
                out.maxX.push(range.maxX);
                out.maxY.push(range.maxY);
            }

            out.minX = Math.min.apply({}, out.minX);
            out.minY = Math.min.apply({}, out.minY);
            out.maxX = Math.max.apply({}, out.maxX);
            out.maxY = Math.max.apply({}, out.maxY);

            return out;
        }
    });

    NS.App.Charts[NAME] = WeightForLengthChart;

}(GC, jQuery));